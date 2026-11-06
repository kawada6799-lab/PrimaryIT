using System.Text.RegularExpressions;
using Kenshin.Config;
using Kenshin.Domain;
using Kenshin.Judge;
using Kenshin.Ocr;
using Kenshin.Reports;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Kenshin.Lab;

/// <summary>紙（mm）と写真（画素）の対応（射影変換）。四隅の印 4 つから求める。</summary>
public sealed class Homography
{
    readonly double[] _h; // 3x3（h33 = 1）
    Homography(double[] h) { _h = h; }

    /// <summary>紙の点 (x,y) → 写真の点。</summary>
    public (double x, double y) Map(double x, double y)
    {
        var w = _h[6] * x + _h[7] * y + 1;
        return ((_h[0] * x + _h[1] * y + _h[2]) / w, (_h[3] * x + _h[4] * y + _h[5]) / w);
    }

    /// <summary>4 組の対応点から求める（DLT、8 元連立）。</summary>
    public static Homography From(IReadOnlyList<(double x, double y)> src, IReadOnlyList<(double x, double y)> dst)
    {
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = src[i]; var (u, v) = dst[i];
            a[2 * i, 0] = x; a[2 * i, 1] = y; a[2 * i, 2] = 1; a[2 * i, 6] = -u * x; a[2 * i, 7] = -u * y; a[2 * i, 8] = u;
            a[2 * i + 1, 3] = x; a[2 * i + 1, 4] = y; a[2 * i + 1, 5] = 1; a[2 * i + 1, 6] = -v * x; a[2 * i + 1, 7] = -v * y; a[2 * i + 1, 8] = v;
        }
        // ガウスの消去法（部分ピボット）
        for (var c = 0; c < 8; c++)
        {
            var p = c; for (var r = c + 1; r < 8; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[p, c])) p = r;
            if (Math.Abs(a[p, c]) < 1e-12) throw new InvalidOperationException("位置合わせの計算ができません（印が一直線など）");
            if (p != c) for (var k = 0; k < 9; k++) (a[c, k], a[p, k]) = (a[p, k], a[c, k]);
            for (var r = 0; r < 8; r++)
            {
                if (r == c) continue;
                var f = a[r, c] / a[c, c];
                for (var k = c; k < 9; k++) a[r, k] -= f * a[c, k];
            }
        }
        var h = new double[8];
        for (var i = 0; i < 8; i++) h[i] = a[i, 8] / a[i, i];
        return new Homography(h);
    }
}

/// <summary>入力シートの 1 項目の読み取り結果。</summary>
public sealed class SheetFieldResult
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>digits：数字の並び（小数点付き）。check：塗られた選択肢（複数なら「;」区切り）</summary>
    public string Value { get; set; } = "";
    public string Status { get; set; } = "empty"; // ok / check / empty
    public string Reason { get; set; } = "";
    public float Score { get; set; }
    /// <summary>マスごとの読み（数字のみ）と墨の量</summary>
    public List<string> CellTexts { get; set; } = new();
    public List<float> CellInk { get; set; } = new();
    /// <summary>確認画面用の切り抜き（JPEG base64）</summary>
    public string? Crop { get; set; }
}

public sealed record SheetFinding(string Section, string Code);

public sealed class SheetReadResult
{
    public string FileName { get; set; } = "";
    public long ExamId { get; set; }
    public string HeaderText { get; set; } = "";
    public List<SheetFieldResult> Fields { get; set; } = new();
    /// <summary>保存する値（キー → 値）。check の選択肢は 1 つだけ塗られているときに入る</summary>
    public Dictionary<string, string> Values { get; set; } = new();
    /// <summary>所見（部位とコード）</summary>
    public List<SheetFinding> Findings { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string Error { get; set; } = "";
    public int CheckCount => Fields.Count(f => f.Status == "check");
}

/// <summary>
/// 入力シートの写真を読む。四隅の黒い四角で紙の位置を合わせ、設計図（SheetTemplate）の座標からマスと □ を切り出す。
/// □ は墨の量だけで判定（AI 不要）。数字のマスは 1 マスずつ OCR（PC 内）。読めたものは人が確認画面で見てから保存する。
/// </summary>
public static class SheetReader
{
    /// <summary>写真の中の四隅の印を探す。見つかれば印の中心（画素）を左上・右上・左下・右下の順で返す。</summary>
    public static List<(double x, double y)>? FindMarkers(Image<Rgb24> img)
    {
        var scale = 1000.0 / img.Width;
        var w = (int)(img.Width * scale); var h = (int)(img.Height * scale);
        using var small = img.Clone(c => c.Resize(w, h).Grayscale());
        var g = new byte[w * h];
        small.ProcessPixelRows(acc => { for (var y = 0; y < h; y++) { var row = acc.GetRowSpan(y); for (var x = 0; x < w; x++) g[y * w + x] = row[x].R; } });
        var sorted = (byte[])g.Clone(); Array.Sort(sorted); var median = sorted[sorted.Length / 2];
        var thr = (int)(median * 0.5);
        var seen = new bool[w * h]; var stack = new Stack<int>();
        var cands = new List<(double cx, double cy, int size)>();
        for (var i = 0; i < g.Length; i++)
        {
            if (seen[i] || g[i] > thr) continue;
            int minx = w, miny = h, maxx = -1, maxy = -1, n = 0; double sx = 0, sy = 0;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                var j = stack.Pop(); var x = j % w; var y = j / w; n++; sx += x; sy += y;
                if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y;
                if (x > 0 && !seen[j - 1] && g[j - 1] <= thr) { seen[j - 1] = true; stack.Push(j - 1); }
                if (x < w - 1 && !seen[j + 1] && g[j + 1] <= thr) { seen[j + 1] = true; stack.Push(j + 1); }
                if (y > 0 && !seen[j - w] && g[j - w] <= thr) { seen[j - w] = true; stack.Push(j - w); }
                if (y < h - 1 && !seen[j + w] && g[j + w] <= thr) { seen[j + w] = true; stack.Push(j + w); }
            }
            var bw = maxx - minx + 1; var bh = maxy - miny + 1;
            if (bw < w * 0.015 || bw > w * 0.09 || bh < w * 0.015 || bh > w * 0.09) continue;
            var aspect = (double)bw / bh; if (aspect < 0.65 || aspect > 1.55) continue;
            if ((double)n / (bw * bh) < 0.75) continue; // 中まで黒い四角
            cands.Add((sx / n, sy / n, bw));
        }
        if (cands.Count < 4) return null;
        // 四隅それぞれに最も近い候補
        var corners = new (double x, double y)[] { (0, 0), (w, 0), (0, h), (w, h) };
        var picked = new List<(double x, double y)>();
        foreach (var (cx, cy) in corners)
        {
            var best = cands.OrderBy(c => (c.cx - cx) * (c.cx - cx) + (c.cy - cy) * (c.cy - cy)).First();
            if (Math.Abs(best.cx - cx) > w * 0.3 || Math.Abs(best.cy - cy) > h * 0.3) return null;
            picked.Add((best.cx / scale, best.cy / scale));
        }
        if (picked.Distinct().Count() < 4) return null;
        return picked;
    }

    /// <summary>紙の範囲（mm）を真上から見た画像に直す（scale 画素/mm）。</summary>
    public static Image<Rgb24> Warp(Image<Rgb24> img, Homography h, double x0, double y0, double wmm, double hmm, double scale)
    {
        var ow = Math.Max(1, (int)Math.Round(wmm * scale)); var oh = Math.Max(1, (int)Math.Round(hmm * scale));
        var outImg = new Image<Rgb24>(ow, oh);
        var W = img.Width; var H = img.Height;
        var srcRows = new Rgb24[H][];
        img.ProcessPixelRows(acc => { for (var y = 0; y < H; y++) srcRows[y] = acc.GetRowSpan(y).ToArray(); });
        outImg.ProcessPixelRows(acc =>
        {
            for (var v = 0; v < oh; v++)
            {
                var row = acc.GetRowSpan(v);
                for (var u = 0; u < ow; u++)
                {
                    var (px, py) = h.Map(x0 + (u + 0.5) / scale, y0 + (v + 0.5) / scale);
                    var xi = (int)Math.Round(px); var yi = (int)Math.Round(py);
                    row[u] = xi >= 0 && yi >= 0 && xi < W && yi < H ? srcRows[yi][xi] : new Rgb24(255, 255, 255);
                }
            }
        });
        return outImg;
    }

    static double Ink(Image<Rgb24> cell, int thr)
    {
        long dark = 0, n = 0;
        cell.ProcessPixelRows(acc => { for (var y = 0; y < cell.Height; y++) { var row = acc.GetRowSpan(y); for (var x = 0; x < cell.Width; x++) { n++; var p = row[x]; if ((p.R + p.G + p.B) / 3 < thr) dark++; } } });
        return n == 0 ? 0 : (double)dark / n;
    }

    static int PageThreshold(Image<Rgb24> img, Homography h)
    {
        // 紙の明るさ：見出しの下の空白帯（mm 40〜44 付近）を見本にする
        using var strip = Warp(img, h, 20, 38, 150, 4, 2);
        var vals = new List<int>();
        strip.ProcessPixelRows(acc => { for (var y = 0; y < strip.Height; y++) { var row = acc.GetRowSpan(y); for (var x = 0; x < strip.Width; x++) { var p = row[x]; vals.Add((p.R + p.G + p.B) / 3); } } });
        vals.Sort(); var med = vals.Count > 0 ? vals[vals.Count / 2] : 200;
        return Math.Max(40, (int)(med * 0.55));
    }

    /// <summary>キー → 画面に出す名前。</summary>
    public static readonly Dictionary<string, string> KeyLabels = new()
    {
        [Keys.Height] = "身長", [Keys.Weight] = "体重", [Keys.Waist] = "腹囲", [Keys.MealMinutes] = "食後時間",
        [Keys.VisionR] = "視力 裸眼 右", [Keys.VisionL] = "視力 裸眼 左", [Keys.VisionRc] = "視力 矯正 右", [Keys.VisionLc] = "視力 矯正 左",
        [Keys.Bp1Sys] = "血圧 1回目 上", [Keys.Bp1Dia] = "血圧 1回目 下", [Keys.Bp2Sys] = "血圧 2回目 上", [Keys.Bp2Dia] = "血圧 2回目 下", [Keys.BpMed] = "降圧剤",
        [Keys.HearR1000] = "聴力 右1000", [Keys.HearR4000] = "聴力 右4000", [Keys.HearL1000] = "聴力 左1000", [Keys.HearL4000] = "聴力 左4000", [Keys.HearConv] = "会話法",
        [Keys.UrineSugar] = "尿 糖", [Keys.UrineProtein] = "尿 蛋白", [Keys.UrineBlood] = "尿 潜血", [Keys.UrineUrobil] = "ｳﾛﾋﾞﾘﾉｰｹﾞﾝ", [Keys.UrineMenstruating] = "生理中",
        [Keys.Stool1] = "便 1回目", [Keys.Stool2] = "便 2回目",
        [Keys.QBpMed] = "問診 血圧の薬", [Keys.QDmMed] = "問診 ｲﾝｽﾘﾝ・血糖の薬", [Keys.QLipidMed] = "問診 ｺﾚｽﾃﾛｰﾙの薬", [Keys.QSmoking] = "問診 タバコ", [Keys.QWeightGain] = "問診 20歳から10kg増", [Keys.QExercise] = "問診 運動",
        [Keys.QAlcoholFreq] = "飲酒", [Keys.QAlcoholAmount] = "飲酒量", [Keys.NextGastroYears] = "次回の胃カメラ", [Keys.NextColonYears] = "次回の大腸カメラ",
    };
    public static string LabelOf(string key) => KeyLabels.TryGetValue(key, out var l) ? l : key.StartsWith("finding:") ? "所見 " + key[8..] : key.StartsWith("finding_code") ? "所見コード " + key[(key.IndexOf(':') + 1)..] : key;

    public static SheetReadResult Read(Image<Rgb24> img, OcrEngine? ocr, Masters masters, ExamContext? ctxForTemplate = null, Func<long, ExamContext?>? examLookup = null, bool withCrops = true)
    {
        var r = new SheetReadResult();
        var marks = FindMarkers(img);
        if (marks == null) { r.Error = "四隅の黒い四角が見つかりません。用紙全体が入るように撮ってください"; return r; }
        var src = SheetTemplate.Marks.Select(m => ((double)m.x + SheetTemplate.MarkSize / 2, (double)m.y + SheetTemplate.MarkSize / 2)).ToList();
        var h = Homography.From(src, marks);
        // 上下逆さまに撮られていないか：QR の場所の墨の量で判定
        var thr = PageThreshold(img, h);
        double QrInk(Homography hh) { using var q = Warp(img, hh, SheetTemplate.QrX, SheetTemplate.QrY, SheetTemplate.QrSize, SheetTemplate.QrSize, 2); return Ink(q, thr); }
        var rotated = Homography.From(src, new[] { marks[3], marks[2], marks[1], marks[0] });
        if (QrInk(rotated) > QrInk(h) * 1.5) { h = rotated; r.Notes.Add("上下逆さまの写真として読んだ"); }
        // 見出し：健診回の番号（印字なので OCR で確実に読める）
        if (ocr != null)
        {
            using var head = Warp(img, h, 14, 6, 150, 24, 6);
            var boxes = ocr.Recognize(head);
            r.HeaderText = string.Join(" ", boxes.OrderBy(b => b.Y0).ThenBy(b => b.X0).Select(b => b.Text));
            var m = Regex.Match(Num.ToHalfWidth(r.HeaderText).Replace(" ", ""), @"健診回(\d+)");
            if (m.Success) r.ExamId = long.Parse(m.Groups[1].Value);
        }
        if (r.ExamId == 0) r.Notes.Add("健診回の番号が読めなかった（受診者を手で選ぶ）");
        // 設計図：その健診回のコース（読めなければ渡されたもの）
        var ctx = r.ExamId != 0 && examLookup != null ? examLookup(r.ExamId) ?? ctxForTemplate : ctxForTemplate;
        if (ctx == null) { r.Error = "健診回が分からないので、どのシートか決められません"; return r; }
        var t = SheetTemplate.Build(ctx, masters);
        // □ とマスを切り出す
        const double cs = 8; // 画素/mm
        var digitImgs = new List<Image<Rgb24>?>(); var digitRefs = new List<(SheetFieldResult f, int idx)>();
        foreach (var f in t.Fields)
        {
            var fr = new SheetFieldResult { Key = f.Key, Kind = f.Kind, Label = f.Kind == "check" ? f.Option : f.Label };
            r.Fields.Add(fr);
            foreach (var c in f.Cells)
            {
                var mx = c.W * 0.18; var my = c.H * 0.15; // 枠線を避けて内側だけ
                var cell = Warp(img, h, c.X + mx, c.Y + my, c.W - 2 * mx, c.H - 2 * my, cs);
                var ink = (float)Ink(cell, thr);
                fr.CellInk.Add(ink);
                if (f.Kind == "digits" && ink > 0.02) { digitImgs.Add(cell); digitRefs.Add((fr, fr.CellInk.Count - 1)); fr.CellTexts.Add("?"); }
                else { fr.CellTexts.Add(""); cell.Dispose(); }
            }
        }
        if (ocr != null && digitImgs.Count > 0)
        {
            var res = ocr.RecognizeImages(digitImgs);
            for (var i = 0; i < res.Length; i++)
            {
                var (f, idx) = digitRefs[i];
                var tx = Num.ToHalfWidth(res[i].text).Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1').Replace('|', '1').Replace('Z', '2').Replace('S', '5').Replace('B', '8').Replace('g', '9').Replace('q', '9');
                tx = new string(tx.Where(char.IsDigit).ToArray());
                f.CellTexts[idx] = tx;
                f.Score = f.Score == 0 ? res[i].score : Math.Min(f.Score, res[i].score);
            }
            foreach (var im in digitImgs) im?.Dispose();
        }
        // 値に組み立てる
        var byKey = new Dictionary<string, List<(SheetField f, SheetFieldResult fr)>>();
        for (var i = 0; i < t.Fields.Count; i++)
        {
            if (!byKey.TryGetValue(t.Fields[i].Key, out var lst)) byKey[t.Fields[i].Key] = lst = new();
            lst.Add((t.Fields[i], r.Fields[i]));
        }
        foreach (var (key, list) in byKey)
        {
            if (list[0].f.Kind == "digits")
            {
                foreach (var (f, fr) in list)
                {
                    AssembleDigits(f, fr, ocr != null);
                    if (fr.Status == "empty") continue;
                    if (key.StartsWith("finding_code")) continue; // 所見コードは下で
                    r.Values[key] = fr.Value;
                }
            }
            else
            {
                var filled = list.Where(x => x.fr.CellInk[0] > 0.22).ToList();
                var faint = list.Where(x => x.fr.CellInk[0] > 0.08 && x.fr.CellInk[0] <= 0.22).ToList();
                foreach (var (f, fr) in list)
                {
                    fr.Value = fr.CellInk[0] > 0.22 ? f.Option : "";
                    fr.Status = fr.CellInk[0] > 0.22 ? "ok" : fr.CellInk[0] > 0.08 ? "check" : "empty";
                    if (fr.Status == "check") fr.Reason = "薄い（塗りかけ？ ✓？）";
                }
                if (key.StartsWith("finding:")) { foreach (var (f, _) in filled) r.Findings.Add(new SheetFinding(key[8..], f.Option)); continue; }
                if (filled.Count == 1 && faint.Count == 0) r.Values[key] = filled[0].f.Option;
                else if (filled.Count > 1) { foreach (var (_, fr) in filled) { fr.Status = "check"; fr.Reason = "同じ項目で 2 つ以上塗られている"; } }
            }
        }
        // 所見コードのマス → 部位のコード（マスターにあるか）
        foreach (var (f, fr) in byKey.Where(k => k.Key.StartsWith("finding_code")).SelectMany(k => k.Value))
        {
            if (fr.Status == "empty") continue;
            var sec = f.Key[(f.Key.IndexOf(':') + 1)..];
            var list = masters.Findings.Where(x => x.Section == sec).ToList();
            var prefix = list.Count > 0 ? new string(list[0].Code.TakeWhile(char.IsLetter).ToArray()) : "";
            var code = prefix + fr.Value.PadLeft(3, '0');
            if (masters.Finding(sec, code) != null) { r.Findings.Add(new SheetFinding(sec, code)); fr.Value = code; if (fr.Status == "ok") fr.Reason = masters.Finding(sec, code)!.Text; }
            else { fr.Status = "check"; fr.Value = code; fr.Reason = "所見マスターに無いコード"; }
        }
        // 数値の範囲（桁の読み違い・書き間違いの網）
        foreach (var fr in r.Fields.Where(f => f.Kind == "digits" && f.Status == "ok"))
        {
            var v = Num.Parse(fr.Value);
            var (lo, hi) = fr.Key switch
            {
                Keys.Height => (100.0, 220.0), Keys.Weight => (20.0, 200.0), Keys.Waist => (40.0, 200.0),
                Keys.VisionR or Keys.VisionL or Keys.VisionRc or Keys.VisionLc => (0.01, 2.0),
                Keys.Bp1Sys or Keys.Bp2Sys => (60.0, 260.0), Keys.Bp1Dia or Keys.Bp2Dia => (30.0, 160.0),
                Keys.MealMinutes => (0.0, 1440.0), _ => (0.0, 999.0),
            };
            if (v == null || v < lo || v > hi) { fr.Status = "check"; fr.Reason = $"値が範囲外（{lo}〜{hi}）"; r.Values.Remove(fr.Key); }
        }
        if (ocr == null) r.Notes.Add("OCR が無いので □ だけ読んだ（数字は手で入れる）");
        // 確認画面用の切り抜き（数字のマス）
        if (withCrops)
            for (var i = 0; i < t.Fields.Count; i++)
            {
                var f = t.Fields[i]; if (f.Kind != "digits" || r.Fields[i].Status == "empty") continue;
                var x0 = f.Cells.Min(c => c.X) - 1; var y0 = f.Cells.Min(c => c.Y) - 1; var x1 = f.Cells.Max(c => c.X + c.W) + 1; var y1 = f.Cells.Max(c => c.Y + c.H) + 1;
                using var c = Warp(img, h, x0, y0, x1 - x0, y1 - y0, 4);
                using var ms = new MemoryStream(); c.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 80 });
                r.Fields[i].Crop = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
        return r;
    }

    /// <summary>マスごとの読みを数値の文字列にする。左の空きマスは無視、途中の空きは要確認。</summary>
    static void AssembleDigits(SheetField f, SheetFieldResult fr, bool hasOcr)
    {
        var n = f.Cells.Count; var intN = n - f.Decimals;
        var inked = fr.CellInk.Select(i => i > 0.02).ToList();
        if (!inked.Any(x => x)) { fr.Status = "empty"; return; }
        if (!hasOcr) { fr.Status = "check"; fr.Reason = "数字は手で入れる"; return; }
        var intCells = Enumerable.Range(0, intN).ToList(); var decCells = Enumerable.Range(intN, f.Decimals).ToList();
        string Part(List<int> idx, bool leading)
        {
            var s = ""; var started = false; var gap = false;
            foreach (var i in idx)
            {
                if (!inked[i]) { if (started) gap = true; continue; }
                if (gap) { fr.Status = "check"; fr.Reason = "途中に空きマスがある"; }
                started = true; s += fr.CellTexts[i];
                if (fr.CellTexts[i].Length != 1) { fr.Status = "check"; fr.Reason = fr.CellTexts[i].Length == 0 ? "読めないマスがある" : "1 マスに 2 文字以上"; }
            }
            return s;
        }
        var ip = Part(intCells, true); var dp = Part(decCells, false);
        if (ip == "" && dp != "") ip = "0";
        fr.Value = dp != "" ? ip + "." + dp : ip;
        if (fr.Status != "check") fr.Status = fr.Score >= 0.8 ? "ok" : "check";
        if (fr.Status == "check" && fr.Reason == "") fr.Reason = "読みの確信度が低い";
        if (fr.Value == "" || fr.Value == ".") { fr.Status = "check"; fr.Reason = "読めなかった"; }
    }
}
