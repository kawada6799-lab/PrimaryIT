using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Kenshin.Ocr;

/// <summary>読み取った 1 つの文字列と、その位置（元画像の画素座標）。</summary>
public sealed record OcrBox(float X0, float Y0, float X1, float Y1, string Text, float Score)
{
    public float Cx => (X0 + X1) / 2; public float Cy => (Y0 + Y1) / 2; public float W => X1 - X0; public float H => Y1 - Y0;
}

/// <summary>
/// 写真から文字を読む（PaddleOCR の ONNX モデルを ONNX Runtime で動かす）。PC の中だけで動き、外部には送らない。
/// 検出（文字のある場所）→ 認識（その場所の文字列）の 2 段。モデルは models/ocr/ の 2 ファイル。無ければ無効。
/// </summary>
public sealed class OcrEngine : IDisposable
{
    readonly InferenceSession _det, _rec;
    readonly string[] _chars;
    public string Status { get; }
    const int MaxSide = 2000;          // 検出に渡す画像の長辺の上限（画素）。大きいほど小さい文字が読めるが遅い
    const float Thresh = 0.3f;        // 文字らしさの地図を 2 値にする閾値
    const float BoxThresh = 0.6f;     // 箱の平均スコアがこれ未満なら捨てる
    const float UnclipRatio = 1.8f;   // 検出した箱を広げる比率（文字が切れないように）
    const int RecHeight = 48;         // 認識モデルの入力の高さ

    OcrEngine(InferenceSession det, InferenceSession rec, string[] chars, string status) { _det = det; _rec = rec; _chars = chars; Status = status; }

    public static string DetPath(string dir) => Path.Combine(dir, "PP-OCRv6_det_small.onnx");
    public static string RecPath(string dir) => Path.Combine(dir, "PP-OCRv6_rec_small.onnx");

    /// <summary>モデルがあれば読み込む。無ければ null（写真の読み取りは使えない）。</summary>
    public static OcrEngine? TryLoad(string modelDir)
    {
        var dp = DetPath(modelDir); var rp = RecPath(modelDir);
        if (!File.Exists(dp) || !File.Exists(rp)) return null;
        var opt = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount) };
        var det = new InferenceSession(dp, opt);
        var rec = new InferenceSession(rp, opt);
        if (!rec.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var dict) || string.IsNullOrEmpty(dict))
            throw new InvalidOperationException("認識モデルに文字の一覧が入っていません: " + rp);
        var chars = dict.Split('\n');
        return new OcrEngine(det, rec, chars, $"有効（{Path.GetFileName(dp)}, {Path.GetFileName(rp)}）");
    }

    public void Dispose() { _det.Dispose(); _rec.Dispose(); }

    /// <summary>画像の中の文字をすべて読む。上から下、左から右の順。認識は縦横比の近い箱をまとめて 1 回で流す（速くするため）。</summary>
    public List<OcrBox> Recognize(Image<Rgb24> img)
    {
        var boxes = Detect(img).Where(b => b.X1 - b.X0 >= img.Width * 0.016f).ToList(); // 「*」のような小さな印は読まない（1 桁の数字はこれより広い）
        var result = new List<OcrBox>();
        const int batch = 8;
        var ordered = boxes.Select(b => (b, ratio: (b.X1 - b.X0) / Math.Max(1f, b.Y1 - b.Y0))).OrderBy(x => x.ratio).ToList();
        for (var i = 0; i < ordered.Count; i += batch)
        {
            var group = ordered.Skip(i).Take(batch).Select(x => x.b).ToList();
            var texts = RecognizeBatch(img, group);
            for (var k = 0; k < group.Count; k++)
                if (texts[k].text.Length > 0) result.Add(new OcrBox(group[k].X0, group[k].Y0, group[k].X1, group[k].Y1, texts[k].text, texts[k].score));
        }
        return result.OrderBy(b => b.Y0).ThenBy(b => b.X0).ToList();
    }

    // ---- 検出 ----
    sealed record Rect(float X0, float Y0, float X1, float Y1);

    List<Rect> Detect(Image<Rgb24> img)
    {
        var scale = Math.Min(1f, (float)MaxSide / Math.Max(img.Width, img.Height));
        var w = Math.Max(32, (int)Math.Round(img.Width * scale / 32) * 32);
        var h = Math.Max(32, (int)Math.Round(img.Height * scale / 32) * 32);
        using var small = img.Clone(c => c.Resize(w, h));
        var input = new DenseTensor<float>(new[] { 1, 3, h, w });
        small.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var p = row[x]; // PaddleOCR は BGR の順で学習している
                    input[0, 0, y, x] = (p.B / 255f - 0.5f) / 0.5f;
                    input[0, 1, y, x] = (p.G / 255f - 0.5f) / 0.5f;
                    input[0, 2, y, x] = (p.R / 255f - 0.5f) / 0.5f;
                }
            }
        });
        using var results = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(_det.InputMetadata.Keys.First(), input) });
        var prob = results.First().AsTensor<float>();
        var ph = prob.Dimensions[2]; var pw = prob.Dimensions[3];
        var map = new float[ph * pw];
        for (var y = 0; y < ph; y++) for (var x = 0; x < pw; x++) map[y * pw + x] = prob[0, 0, y, x];
        var sx = (float)img.Width / pw; var sy = (float)img.Height / ph;
        return Components(map, pw, ph).Select(r => new Rect(
            Math.Clamp(r.X0 * sx, 0, img.Width - 1), Math.Clamp(r.Y0 * sy, 0, img.Height - 1),
            Math.Clamp(r.X1 * sx, 0, img.Width), Math.Clamp(r.Y1 * sy, 0, img.Height))).ToList();
    }

    /// <summary>2 値にした地図のつながった塊ごとに箱を作り、平均スコアで選び、少し広げる。</summary>
    static List<Rect> Components(float[] map, int w, int h)
    {
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        var rects = new List<Rect>();
        for (var i = 0; i < map.Length; i++)
        {
            if (seen[i] || map[i] <= Thresh) continue;
            int minx = w, miny = h, maxx = -1, maxy = -1, n = 0; double sum = 0;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                var j = stack.Pop(); var x = j % w; var y = j / w;
                n++; sum += map[j];
                if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y;
                if (x > 0 && !seen[j - 1] && map[j - 1] > Thresh) { seen[j - 1] = true; stack.Push(j - 1); }
                if (x < w - 1 && !seen[j + 1] && map[j + 1] > Thresh) { seen[j + 1] = true; stack.Push(j + 1); }
                if (y > 0 && !seen[j - w] && map[j - w] > Thresh) { seen[j - w] = true; stack.Push(j - w); }
                if (y < h - 1 && !seen[j + w] && map[j + w] > Thresh) { seen[j + w] = true; stack.Push(j + w); }
            }
            var bw = maxx - minx + 1; var bh = maxy - miny + 1;
            if (Math.Min(bw, bh) < 3) continue;
            // 箱の中の平均スコア（箱の中の全画素）
            double s = 0; var cnt = 0;
            for (var y = miny; y <= maxy; y++) for (var x = minx; x <= maxx; x++) { s += map[y * w + x]; cnt++; }
            if (s / cnt < BoxThresh && sum / n < BoxThresh + 0.1) continue;
            // 広げる：面積 × 比率 ÷ 周長（DB の unclip と同じ考え方）
            var d = (float)(bw * bh * UnclipRatio / (2.0 * (bw + bh)));
            rects.Add(new Rect(minx - d, miny - d, maxx + 1 + d, maxy + 1 + d));
        }
        return rects;
    }

    // ---- 認識 ----
    (string text, float score)[] RecognizeBatch(Image<Rgb24> img, List<Rect> rects)
    {
        var n = rects.Count;
        var crops = new List<(Image<Rgb24>? img, int w)>();
        var maxRatio = 320f / RecHeight;
        foreach (var r in rects)
        {
            var x0 = (int)Math.Floor(r.X0); var y0 = (int)Math.Floor(r.Y0);
            var cw = Math.Max(1, (int)Math.Ceiling(r.X1) - x0); var ch = Math.Max(1, (int)Math.Ceiling(r.Y1) - y0);
            if (ch < 8 || cw < 4) { crops.Add((null, 0)); continue; }
            var ratio = (float)cw / ch; maxRatio = Math.Max(maxRatio, ratio);
            var rw = Math.Max(1, (int)Math.Ceiling(RecHeight * ratio));
            crops.Add((img.Clone(c => c.Crop(new Rectangle(x0, y0, cw, ch)).Resize(rw, RecHeight)), rw));
        }
        var width = (int)Math.Ceiling(RecHeight * maxRatio);
        var input = new DenseTensor<float>(new[] { n, 3, RecHeight, width });
        for (var k = 0; k < n; k++)
        {
            var (crop, rw) = crops[k]; if (crop == null) continue;
            var kk = k;
            crop.ProcessPixelRows(acc =>
            {
                for (var y = 0; y < RecHeight; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (var x = 0; x < rw; x++)
                    {
                        var p = row[x];
                        input[kk, 0, y, x] = (p.B / 255f - 0.5f) / 0.5f;
                        input[kk, 1, y, x] = (p.G / 255f - 0.5f) / 0.5f;
                        input[kk, 2, y, x] = (p.R / 255f - 0.5f) / 0.5f;
                    }
                }
            });
            crop.Dispose();
        }
        using var results = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_rec.InputMetadata.Keys.First(), input) });
        var outp = results.First().AsTensor<float>();
        var steps = outp.Dimensions[1]; var classes = outp.Dimensions[2];
        var res = new (string, float)[n];
        for (var k = 0; k < n; k++)
        {
            if (crops[k].img == null && crops[k].w == 0) { res[k] = ("", 0); continue; }
            var sb = new System.Text.StringBuilder(); var last = 0; double conf = 0; var cnt = 0;
            for (var t = 0; t < steps; t++)
            {
                var best = 0; var bp = outp[k, t, 0];
                for (var c = 1; c < classes; c++) { var p = outp[k, t, c]; if (p > bp) { bp = p; best = c; } }
                if (best != 0 && best != last) { sb.Append(best - 1 < _chars.Length ? _chars[best - 1] : " "); conf += bp; cnt++; }
                last = best;
            }
            res[k] = (sb.ToString().Trim(), cnt == 0 ? 0 : (float)(conf / cnt));
        }
        return res;
    }
}
