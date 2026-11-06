using Kenshin.Lab;
using Kenshin.Ocr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace Kenshin.Tests;

/// <summary>開発者の手元だけで動く確認（KENSHIN_OCR_PHOTOS に写真フォルダがあるとき）。リポジトリに写真は入れない。</summary>
public class OcrPhotoDevTests
{
    readonly ITestOutputHelper _o;
    public OcrPhotoDevTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void 写真を読む()
    {
        var dirPhotos = Environment.GetEnvironmentVariable("KENSHIN_OCR_PHOTOS");
        if (string.IsNullOrEmpty(dirPhotos) || !Directory.Exists(dirPhotos)) return;
        var dir = TestEnv.OcrModelDir; if (dir == null) return;
        var eng = OcrEngine.TryLoad(dir)!;
        var masters = TestEnv.Masters();
        foreach (var photo in Directory.GetFiles(dirPhotos, "*.jpg").OrderBy(f => f))
        {
            using var img = Image.Load<Rgb24>(photo);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var boxes = eng.Recognize(img);
            var t1 = sw.ElapsedMilliseconds;
            var r = FalcoPhotoReader.Read(boxes, img.Width, img.Height, masters);
            _o.WriteLine($"=== {Path.GetFileName(photo)} {img.Width}x{img.Height} boxes={boxes.Count} ocr={t1}ms total={sw.ElapsedMilliseconds}ms err={r.Error}");
            _o.WriteLine($"  kana={r.NameKana} sex={r.Sex} no={r.PatientNo} req={r.RequestNo} page={r.Page} taken={r.TakenAt} reported={r.ReportedAt} birth={r.BirthHint} notes={string.Join(";", r.Notes)}");
            _o.WriteLine($"  items={r.Items.Count} ok={r.OkCount} check={r.CheckCount}");
            foreach (var it in r.Items) _o.WriteLine($"  [{it.Status}] {it.Item} = {it.Value} | L {it.Left} ({it.LeftScore:F2}) R {it.Right} ({it.RightScore:F2}) {it.Flag} | {it.Reason}");
            if (Environment.GetEnvironmentVariable("KENSHIN_OCR_DUMP") == "1") foreach (var b in boxes.Where(b => b.Y0 > img.Height * 0.2 && b.Y0 < img.Height * 0.3)) _o.WriteLine($"    box {(int)b.X0},{(int)b.Y0} {(int)b.W}x{(int)b.H} {b.Score:F2} {b.Text}");
        }
    }
}
