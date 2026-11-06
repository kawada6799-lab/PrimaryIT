using QuestPDF.Fluent;
using Kenshin.Config;
using Kenshin.Data;
using Kenshin.Domain;
using Kenshin.Reports;
using Kenshin.Web;
using Xunit;

namespace Kenshin.Tests;

public class ReportTests
{
    public static (AppState state, ExamService svc, long examId, string path) SetupPublic() => Setup();
    static (AppState state, ExamService svc, long examId, string path) Setup()
    {
        var baseDir = Path.GetDirectoryName(TestEnv.ConfigDir)!; // リポジトリのケースフォルダ（config の親）
        var cfg = new AppConfig { BaseDir = baseDir, ConfigDir = TestEnv.ConfigDir, DataDir = TestEnv.TempFile("-data"), NetworkGuard = false };
        cfg.Clinic.Name = "テストクリニック"; cfg.Clinic.Doctor = "医師 太郎";
        var state = new AppState(cfg, Masters.Load(TestEnv.ConfigDir), Criteria.Load(TestEnv.ConfigDir), new NetworkGuard(false));
        Assert.True(state.Unlock("pw"));
        var db = state.RequireDb();
        var pid = db.SavePatient(new Patient { Name = "健診 太郎", Kana = "ケンシン タロウ", Sex = "男", BirthDate = "1974-05-01", FalcoId = "0001", Office = "架空商事" });
        // 前回
        var prev = db.SaveExam(new Exam { PatientId = pid, ExamDate = "2025-11-05", Course = "人間ドック", Status = ExamStatus.Confirmed });
        db.SetValue(prev, Keys.Height, "170.0"); db.SetValue(prev, Keys.Weight, "72.0");
        db.ReplaceBlood(prev, new[] { new BloodResult { Item = "AST (GOT)", Value = "25", Unit = "U/L", RefText = "8~38", RefLow = 8, RefHigh = 38 } });
        // 今回
        var now = db.SaveExam(new Exam { PatientId = pid, ExamDate = "2026-11-04", Course = "人間ドック" });
        foreach (var (k, v) in new Dictionary<string, string>
        {
            [Keys.Height] = "170.2", [Keys.Weight] = "74.5", [Keys.Waist] = "88.0", [Keys.VisionR] = "1.2", [Keys.VisionL] = "0.8",
            [Keys.HearR1000] = "なし", [Keys.HearR4000] = "なし", [Keys.HearL1000] = "なし", [Keys.HearL4000] = "なし",
            [Keys.Bp1Sys] = "142", [Keys.Bp1Dia] = "91", [Keys.Bp2Sys] = "138", [Keys.Bp2Dia] = "88",
            [Keys.UrineSugar] = "−", [Keys.UrineProtein] = "±", [Keys.UrineBlood] = "−", [Keys.UrineUrobil] = "正常", [Keys.Stool1] = "−", [Keys.Stool2] = "−",
            [Keys.QSmoking] = "1", [Keys.NextGastroYears] = "1", [Keys.NextColonYears] = "2", [Keys.FreeComment] = "架空のコメント",
        }) db.SetValue(now, k, v);
        db.AddFinding(new Finding { ExamId = now, Section = Sections.Gastroscopy, Code = "V014", Text = "胃ポリープ" });
        db.AddFinding(new Finding { ExamId = now, Section = Sections.ChestXray, Code = "X001", Text = "異常を認めず" });
        db.AddFinding(new Finding { ExamId = now, Section = Sections.Ecg, Code = "E001", Text = "正常範囲" });
        db.AddFinding(new Finding { ExamId = now, Section = Sections.Physical, Code = "D001", Text = "異常を認めず" });
        db.ReplaceBlood(now, new[]
        {
            new BloodResult { Item = "AST (GOT)", Value = "27", Unit = "U/L", RefText = "8~38", RefLow = 8, RefHigh = 38 },
            new BloodResult { Item = "ALT (GPT)", Value = "48", Unit = "U/L", RefText = "4~43", RefLow = 4, RefHigh = 43, Flag = "↑" },
            new BloodResult { Item = "中性脂肪 (TG)", Value = "162", Unit = "mg/dL", RefText = "30~149", RefLow = 30, RefHigh = 149, Flag = "↑" },
            new BloodResult { Item = "HDLコレステロール", Value = "42", Unit = "mg/dL", RefText = "40~77", RefLow = 40, RefHigh = 77 },
            new BloodResult { Item = "血糖", Value = "104", Unit = "mg/dL", RefText = "60~109", RefLow = 60, RefHigh = 109 },
            new BloodResult { Item = "HbA1c NGSP", Value = "5.8", Unit = "%", RefText = "4.6~6.2", RefLow = 4.6, RefHigh = 6.2 },
            new BloodResult { Item = "CEA", Value = "0.6", Unit = "ng/mL", RefText = "5.0以下", RefHigh = 5.0 },
        });
        var svc = new ExamService(state);
        svc.Recompute(now);
        return (state, svc, now, cfg.DataDir);
    }

    [Fact]
    public void 三種の帳票が生成でき_確定で保存される()
    {
        var (state, svc, examId, dataDir) = Setup();
        try
        {
            foreach (var kind in new[] { "jushinhyo", "notice", "explanation" })
            {
                var bytes = ReportService.Render(state, svc, examId, kind);
                Assert.True(bytes.Length > 2000, kind);
                Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
                File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"kenshin-test-{kind}.pdf"), bytes);
            }
            var path = ReportService.SaveNotice(state, svc, examId);
            Assert.True(File.Exists(path));
            Assert.Single(state.RequireDb().IssuedPdfs(examId));
            // 判定の中身
            var dto = svc.Dto(examId);
            Assert.NotNull(dto);
            var js = state.RequireDb().GetJudgments(examId);
            Assert.Equal("180", js.First(j => j.Section == Sections.Measure).Code);
            Assert.Equal("180", js.First(j => j.Section == Sections.Overall).Code); // 要経過観察（計測・血圧・胃）が要再検査（尿）より重い（現物の結果のお知らせに合わせた順）
            Assert.Equal(ExamStatus.Ready, state.RequireDb().GetExam(examId)!.Status);
        }
        finally { state.Dispose(); try { Directory.Delete(dataDir, true); } catch { } }
    }

    [Fact]
    public void 印刷した文字PDFを読み戻せる()
    {
        // ファルコ報告書と同じ並びの架空 PDF を作って、読み取りが行を拾えることを確かめる（HR-f の印刷の代わり）
        var (state, svc, examId, dataDir) = Setup();
        try
        {
            ReportService.Init(state.Config.BaseDir);
            var pdf = QuestPDF.Fluent.Document.Create(c => c.Page(p =>
            {
                p.Size(QuestPDF.Helpers.PageSizes.A4); p.Margin(20);
                p.DefaultTextStyle(t => t.FontFamily(ReportService.Font).FontSize(10));
                p.Content().Column(col =>
                {
                    col.Item().Text("検査報告書  00-625-250(2/2)");
                    col.Item().Text("個人ID 0001  健診 太郎 様");
                    col.Item().Text("受付日 '26/10/19 報告日 '26/10/20 採取日 '26/10/19 12:47");
                    col.Item().Text("検査項目  検査結果  単位  基準値");
                    foreach (var line in new[] { "白血球数  54  ×10² /μL  39 ~ 98", "ヘモグロビン量  15.6  g/dL  13.5~ 17.6", "HbA1c NGSP  ↑  8.4  %  4.6~6.2", "CRP定量  0.05以下  mg/dL  0.30以下", "HBs抗原 精密測定  陰性  インセイ" })
                        col.Item().Row(r => { foreach (var cell in line.Split("  ")) r.RelativeItem().Text(cell); });
                });
            })).GeneratePdf();
            var rep = FalcoPdfReaderFacade.Read(pdf);
            Assert.Equal("", rep.Error);
            Assert.Equal("2026-10-19", rep.TakenAt);
            Assert.Equal("0001", rep.IdHint);
            Assert.True(rep.Items.Count >= 5, string.Join("\n", rep.Lines));
            Assert.Equal("↑", rep.Items.First(i => i.Item.Contains("HbA1c")).Flag);
            Assert.Equal("15.6", rep.Items.First(i => i.Item.Contains("ヘモグロビン")).Value);
        }
        finally { state.Dispose(); try { Directory.Delete(dataDir, true); } catch { } }
    }

    static class FalcoPdfReaderFacade { public static Lab.ParsedLabReport Read(byte[] b) => Lab.FalcoPdfReader.ReadPdf(b); }
}

public static class ReportTestsFacade { public static (Kenshin.Web.AppState state, Kenshin.Web.ExamService svc, long examId, string path) Setup() => ReportTests.SetupPublic(); }
