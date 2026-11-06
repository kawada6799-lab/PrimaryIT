using Kenshin.Config;
using Kenshin.Data;
using Kenshin.Domain;
using Kenshin.Lab;
using Kenshin.Ocr;
using Kenshin.Reports;
using Kenshin.Web;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Image = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace Kenshin.Tests;

/// <summary>入力シート：設計図、記入済みシートを写真のように歪ませて読み戻す。患者データは架空。</summary>
public class SheetReaderTests
{
    readonly ITestOutputHelper _o;
    public SheetReaderTests(ITestOutputHelper o) { _o = o; }

    static (AppState state, ExamService svc, long examId) Setup()
    {
        var baseDir = TestEnv.CaseDir;
        var cfg = new AppConfig { BaseDir = baseDir, ConfigDir = TestEnv.ConfigDir, DataDir = TestEnv.TempFile("-data"), NetworkGuard = false };
        cfg.Clinic.Name = "テストクリニック";
        var state = new AppState(cfg, Masters.Load(TestEnv.ConfigDir), Criteria.Load(TestEnv.ConfigDir), new NetworkGuard(false));
        Assert.True(state.Unlock("pw"));
        var db = state.RequireDb();
        var pid = db.SavePatient(new Patient { Name = "健診 太郎", Kana = "ケンシン タロウ", Sex = "男", BirthDate = "1974-05-01", FalcoId = "0001" });
        var examId = db.SaveExam(new Exam { PatientId = pid, ExamDate = "2026-11-06", Course = "人間ドック" });
        var svc = new ExamService(state);
        return (state, svc, examId);
    }

    [Fact]
    public void 射影変換は4点を正確に写す()
    {
        var src = new (double, double)[] { (0, 0), (100, 0), (0, 200), (100, 200) };
        var dst = new (double, double)[] { (10, 20), (310, 30), (5, 620), (330, 640) };
        var h = Homography.From(src, dst);
        for (var i = 0; i < 4; i++) { var (x, y) = h.Map(src[i].Item1, src[i].Item2); Assert.InRange(x, dst[i].Item1 - 0.01, dst[i].Item1 + 0.01); Assert.InRange(y, dst[i].Item2 - 0.01, dst[i].Item2 + 0.01); }
        var (mx, my) = h.Map(50, 100); Assert.InRange(mx, 100, 250); Assert.InRange(my, 250, 400);
    }

    [Fact]
    public void 設計図は受診者のマスを持ち_コースに関係なく同じで_マスは紙の中に収まる()
    {
        var (state, svc, examId) = Setup();
        var ctx = svc.Context(examId)!;
        var t = SheetTemplate.Build(ctx, state.Masters);
        var blank = SheetTemplate.Build(null, state.Masters);
        Assert.Equal(t.Fields.Select(f => (f.Key, f.Option, f.Cells[0].X, f.Cells[0].Y)), blank.Fields.Select(f => (f.Key, f.Option, f.Cells[0].X, f.Cells[0].Y)));
        Assert.Contains(t.Fields, f => f.Key == SheetTemplate.KeyFalcoId && f.Cells.Count == 6);
        Assert.Contains(t.Fields, f => f.Key == SheetTemplate.KeyDate && f.Cells.Count == 8 && f.Decimals == 0);
        Assert.Contains(t.Fields, f => f.Key == SheetTemplate.KeyBirth && f.Cells.Count == 8);
        Assert.Contains(t.Fields, f => f.Key == SheetTemplate.KeySex && f.Kind == "check" && f.Option == "女");
        // 受診者のマスは QR（右上）と重ならない
        foreach (var c in t.Fields.Where(f => SheetTemplate.IsHeaderKey(f.Key)).SelectMany(f => f.Cells)) Assert.True(c.Y >= SheetTemplate.QrY + SheetTemplate.QrSize + 1 || c.X + c.W < SheetTemplate.QrX - 1, $"{c} が QR と重なる");
        Assert.Contains(t.Fields, f => f.Key == Keys.Stool1); // コースに無い検査も載せる（白紙と同じ並びにするため）
        Assert.Contains(t.Fields, f => f.Key == Keys.Height && f.Kind == "digits" && f.Cells.Count == 4 && f.Decimals == 1);
        Assert.Contains(t.Fields, f => f.Key == Keys.UrineSugar && f.Kind == "check" && f.Option == "±");
        Assert.DoesNotContain(t.Fields, f => f.Key.StartsWith("finding")); // 所見は先生が画面で入れる。シートは看護師の分だけ
        Assert.DoesNotContain(t.Fields, f => f.Key == Keys.QSmoking);
        foreach (var c in t.Fields.SelectMany(f => f.Cells)) { Assert.InRange(c.X, 10, SheetTemplate.PageW - 10); Assert.InRange(c.Y + c.H, 40, SheetTemplate.PageH - 20); }
        // マスが重ならない
        var cells = t.Fields.SelectMany(f => f.Cells).ToList();
        for (var i = 0; i < cells.Count; i++) for (var j = i + 1; j < cells.Count; j++)
            Assert.False(cells[i].X < cells[j].X + cells[j].W - 0.1 && cells[j].X < cells[i].X + cells[i].W - 0.1 && cells[i].Y < cells[j].Y + cells[j].H - 0.1 && cells[j].Y < cells[i].Y + cells[i].H - 0.1, $"マスが重なる {i} {j}");
    }

    /// <summary>記入済みのシートを画像にし、写真のように傾け・歪ませる。</summary>
    static Image<Rgb24> FilledPhoto(AppState state, ExamService svc, long examId, Dictionary<string, string> prefill, double skew)
    {
        ReportService.Init(state.Config.BaseDir);
        var d = ReportService.Gather(state, svc, examId);
        var png = new InputSheetDocument(d, prefill).GenerateImages(new ImageGenerationSettings { RasterDpi = 150 }).First();
        if (Environment.GetEnvironmentVariable("KENSHIN_SHEET_DUMP") is { Length: > 0 } dump) File.WriteAllBytes(dump, png);
        using var flat = SixLabors.ImageSharp.Image.Load<Rgb24>(png);
        return Distort(flat, skew);
    }

    /// <summary>紙の画像を写真のように傾け・歪ませる。</summary>
    static Image<Rgb24> Distort(Image<Rgb24> flat, double skew)
    {
        // 紙の四隅 → 写真の中の歪んだ四角形（少し回転・台形）へ写す
        var W = flat.Width; var H = flat.Height;
        var canvasW = (int)(W * 1.25); var canvasH = (int)(H * 1.2);
        var dst = new (double, double)[] { (W * 0.10, H * 0.06), (W * 1.12 + skew * W * 0.03, H * 0.09), (W * 0.06 + skew * W * 0.02, H * 1.10), (W * 1.16, H * 1.13 - skew * H * 0.02) };
        var hFlatToPhoto = Homography.From(new (double, double)[] { (0, 0), (W, 0), (0, H), (W, H) }, dst);
        // 写真 → 紙の逆写像で描く（紙の画素を参照）
        var hPhotoToFlat = Homography.From(dst, new (double, double)[] { (0, 0), (W, 0), (0, H), (W, H) });
        var photo = new Image<Rgb24>(canvasW, canvasH, new Rgb24(120, 110, 100));
        var rows = new Rgb24[H][]; flat.ProcessPixelRows(a => { for (var y = 0; y < H; y++) rows[y] = a.GetRowSpan(y).ToArray(); });
        photo.ProcessPixelRows(acc =>
        {
            for (var v = 0; v < canvasH; v++)
            {
                var row = acc.GetRowSpan(v);
                for (var u = 0; u < canvasW; u++)
                {
                    var (fx, fy) = hPhotoToFlat.Map(u, v);
                    var xi = (int)Math.Round(fx); var yi = (int)Math.Round(fy);
                    if (xi >= 0 && yi >= 0 && xi < W && yi < H) { var p = rows[yi][xi]; row[u] = new Rgb24((byte)(p.R * 0.92), (byte)(p.G * 0.92), (byte)(p.B * 0.90)); } // 少し暗く（紙の影）
                }
            }
        });
        return photo;
    }

    [Fact]
    public void 記入済みシートを歪ませて読み戻す()
    {
        var (state, svc, examId) = Setup();
        var prefill = new Dictionary<string, string>
        {
            [Keys.Height] = "168.6", [Keys.Weight] = "56.6", [Keys.Waist] = "74.0", [Keys.VisionR] = "0.4", [Keys.VisionL] = "0.7",
            [Keys.Bp1Sys] = "143", [Keys.Bp1Dia] = "88", [Keys.Bp2Sys] = "110", [Keys.Bp2Dia] = "77", [Keys.MealMinutes] = "150",
            [Keys.HearR1000] = "なし", [Keys.HearR4000] = "あり", [Keys.HearL1000] = "なし", [Keys.HearL4000] = "なし",
            [Keys.UrineSugar] = "−", [Keys.UrineProtein] = "±", [Keys.UrineBlood] = "−", [Keys.UrineUrobil] = "正常", [Keys.Stool1] = "−", [Keys.Stool2] = "−",
            [Keys.BpMed] = "1", [Keys.HearConv] = "正常",
        };
        using var photo = FilledPhoto(state, svc, examId, prefill, 1.0);
        if (Environment.GetEnvironmentVariable("KENSHIN_SHEET_DUMP") is { Length: > 0 } dump) photo.Save(dump.Replace(".png", "-photo.jpg"), new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
        var marks = SheetReader.FindMarkers(photo);
        Assert.NotNull(marks);
        var ocr = TestEnv.OcrModelDir != null ? OcrEngine.TryLoad(TestEnv.OcrModelDir) : null;
        var r = SheetReader.Read(photo, ocr, state.Masters, null, id => svc.Context(id));
        _o.WriteLine($"err={r.Error} exam={r.ExamId} header={r.HeaderText} notes={string.Join(";", r.Notes)}");
        foreach (var f in r.Fields.Where(f => f.Status != "empty")) _o.WriteLine($"  [{f.Status}] {f.Key} {f.Label} = {f.Value} ink={string.Join(",", f.CellInk.Select(i => i.ToString("F2")))} cells={string.Join("|", f.CellTexts)} {f.Reason}");
        Assert.Equal("", r.Error);
        // □（AI 不要）はすべて読める
        foreach (var k in new[] { Keys.HearR1000, Keys.HearR4000, Keys.UrineSugar, Keys.UrineProtein, Keys.UrineUrobil, Keys.Stool1, Keys.BpMed, Keys.HearConv })
            Assert.Equal(prefill[k], r.Values.GetValueOrDefault(k));
        Assert.DoesNotContain(Keys.UrineMenstruating, r.Values.Keys);
        // 受診者のマス（印字済み）が読める
        Assert.Equal("男", r.Header.GetValueOrDefault(SheetTemplate.KeySex));
        Assert.DoesNotContain(SheetTemplate.KeySex, r.Values.Keys);
        if (ocr != null)
        {
            Assert.Equal("0001", r.Header.GetValueOrDefault(SheetTemplate.KeyFalcoId));
            Assert.Equal("2026-11-06", r.Header.GetValueOrDefault(SheetTemplate.KeyDate));
            Assert.Equal("1974-05-01", r.Header.GetValueOrDefault(SheetTemplate.KeyBirth));
            Assert.Equal(examId, r.ExamId);
            foreach (var k in new[] { Keys.Height, Keys.Weight, Keys.Waist, Keys.VisionR, Keys.VisionL, Keys.Bp1Sys, Keys.Bp1Dia, Keys.Bp2Sys, Keys.Bp2Dia, Keys.MealMinutes })
            {
                var f = r.Fields.First(x => x.Key == k);
                Assert.True(f.Value == prefill[k] || f.Value + ".0" == prefill[k], $"{k}: 正 {prefill[k]} 読 {f.Value} ({f.Status} {f.Reason})");
            }
            ocr.Dispose();
        }
    }

    /// <summary>白紙のシートに受診者のマスを手書き風に書いたもの。</summary>
    static Image<Rgb24> BlankFilledPhoto(AppState state, Dictionary<string, string> prefill, double skew)
    {
        ReportService.Init(state.Config.BaseDir);
        var png = new InputSheetDocument(state, state.Masters, null, prefill, handwriting: true).GenerateImages(new ImageGenerationSettings { RasterDpi = 150 }).First();
        if (Environment.GetEnvironmentVariable("KENSHIN_SHEET_DUMP") is { Length: > 0 } dump) File.WriteAllBytes(dump.Replace(".png", "-blank.png"), png);
        using var flat = SixLabors.ImageSharp.Image.Load<Rgb24>(png);
        return Distort(flat, skew);
    }

    [Fact]
    public void 白紙シートの受診者マスから_既存の受診者と健診回に決まる_無ければ新規の案内()
    {
        var (state, svc, examId) = Setup();
        var db = state.RequireDb();
        var ocr = TestEnv.OcrModelDir != null ? OcrEngine.TryLoad(TestEnv.OcrModelDir) : null;
        if (ocr == null) return; // 数字は OCR が要る
        var prefill = new Dictionary<string, string>
        {
            [SheetTemplate.KeyFalcoId] = "0001", [SheetTemplate.KeyDate] = "20261106", [SheetTemplate.KeyBirth] = "19740501", [SheetTemplate.KeySex] = "男",
            [Keys.Height] = "171.2", [Keys.Weight] = "64.0", [Keys.Bp1Sys] = "128", [Keys.Bp1Dia] = "82", [Keys.UrineProtein] = "−",
        };
        using var photo = BlankFilledPhoto(state, prefill, 0.8);
        var r = SheetReader.Read(photo, ocr, state.Masters, null, id => svc.Context(id));
        _o.WriteLine($"err={r.Error} exam={r.ExamId} header={string.Join(",", r.Header.Select(kv => kv.Key + "=" + kv.Value))} notes={string.Join(";", r.Notes)}");
        foreach (var f in r.Fields.Where(f => f.Status != "empty" && SheetTemplate.IsHeaderKey(f.Key))) _o.WriteLine($"  [{f.Status}] {f.Key} = {f.Value} cells={string.Join("|", f.CellTexts)} {f.Reason}");
        Assert.Equal("", r.Error);
        Assert.Equal(0, r.ExamId); // 白紙なので健診回の番号は無い
        Assert.Equal("0001", r.Header[SheetTemplate.KeyFalcoId]);
        Assert.Equal("2026-11-06", r.Header[SheetTemplate.KeyDate]);
        Assert.Equal("1974-05-01", r.Header[SheetTemplate.KeyBirth]);
        Assert.Equal("男", r.Header[SheetTemplate.KeySex]);
        Assert.Equal("171.2", r.Fields.First(f => f.Key == Keys.Height).Value);
        // 突合：個人ID で既存の受診者 → 実施日の健診回
        var sg = PhotoImporter.Match(db, r.Header);
        Assert.Equal(examId, sg.ExamId);
        // 個人ID が無くても生年月日＋性別で決まる。実施日の健診回が無ければ「作る」の案内
        var sg2 = PhotoImporter.Match(db, new Dictionary<string, string> { [SheetTemplate.KeyBirth] = "1974-05-01", [SheetTemplate.KeySex] = "男", [SheetTemplate.KeyDate] = "2026-12-01" });
        Assert.NotNull(sg2.PatientId); Assert.Null(sg2.ExamId); Assert.Contains("まだ無い", sg2.Note);
        // 誰にも一致しなければ新規登録の案内
        var sg3 = PhotoImporter.Match(db, new Dictionary<string, string> { [SheetTemplate.KeyFalcoId] = "9999", [SheetTemplate.KeyBirth] = "1990-01-01", [SheetTemplate.KeySex] = "女", [SheetTemplate.KeyDate] = "2026-11-06" });
        Assert.Null(sg3.PatientId); Assert.Contains("新しく登録", sg3.Note);
    }
}
