using Kenshin.Config;
using Kenshin.Domain;
using Kenshin.Reports;
using Kenshin.Web;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Xunit;

namespace Kenshin.Tests;

/// <summary>
/// テスト撮影用の「手書きを模した入力シート」を作る（KENSHIN_TESTSET_DIR にフォルダがあるときだけ）。
/// 受診者は架空の 3 人。採血の報告書の模擬（ファルコの並び）は開発者の手元のスクリプトで同じ 3 人分を作る。
/// </summary>
public class TestSetDevTests
{
    /// <summary>架空の 3 人（個人ID・氏名・カナ・性別・生年月日）と、入力シートに手書きする値。</summary>
    public static readonly (string id, string name, string kana, string sex, string birth, Dictionary<string, string> sheet)[] People =
    {
        ("1001", "テスト 一郎", "テスト イチロウ", "男", "1968-03-12", new()
        {
            [Keys.Height] = "171.4", [Keys.Weight] = "78.2", [Keys.Waist] = "91.5", [Keys.VisionR] = "0.6", [Keys.VisionL] = "0.5", [Keys.VisionRc] = "1.2", [Keys.VisionLc] = "1.0",
            [Keys.Bp1Sys] = "148", [Keys.Bp1Dia] = "94", [Keys.Bp2Sys] = "142", [Keys.Bp2Dia] = "90", [Keys.MealMinutes] = "180", [Keys.BpMed] = "1",
            [Keys.HearR1000] = "なし", [Keys.HearR4000] = "あり", [Keys.HearL1000] = "なし", [Keys.HearL4000] = "なし", [Keys.HearConv] = "正常",
            [Keys.UrineSugar] = "−", [Keys.UrineProtein] = "−", [Keys.UrineBlood] = "−", [Keys.UrineUrobil] = "正常", [Keys.Stool1] = "−", [Keys.Stool2] = "−",
        }),
        ("1002", "テスト 花子", "テスト ハナコ", "女", "1981-07-24", new()
        {
            [Keys.Height] = "158.0", [Keys.Weight] = "51.3", [Keys.Waist] = "72.0", [Keys.VisionR] = "1.2", [Keys.VisionL] = "1.0",
            [Keys.Bp1Sys] = "112", [Keys.Bp1Dia] = "68", [Keys.Bp2Sys] = "108", [Keys.Bp2Dia] = "66", [Keys.MealMinutes] = "720",
            [Keys.HearR1000] = "なし", [Keys.HearR4000] = "なし", [Keys.HearL1000] = "なし", [Keys.HearL4000] = "なし", [Keys.HearConv] = "正常",
            [Keys.UrineSugar] = "−", [Keys.UrineProtein] = "−", [Keys.UrineBlood] = "±", [Keys.UrineUrobil] = "正常", [Keys.UrineMenstruating] = "1", [Keys.Stool1] = "−", [Keys.Stool2] = "−",
        }),
        ("1003", "テスト 次郎", "テスト ジロウ", "男", "1995-12-03", new()
        {
            [Keys.Height] = "176.9", [Keys.Weight] = "64.5", [Keys.Waist] = "78.5", [Keys.VisionR] = "0.3", [Keys.VisionL] = "0.4", [Keys.VisionRc] = "1.5", [Keys.VisionLc] = "1.5",
            [Keys.Bp1Sys] = "124", [Keys.Bp1Dia] = "76", [Keys.MealMinutes] = "90",
            [Keys.HearR1000] = "なし", [Keys.HearR4000] = "なし", [Keys.HearL1000] = "なし", [Keys.HearL4000] = "なし", [Keys.HearConv] = "正常",
            [Keys.UrineSugar] = "−", [Keys.UrineProtein] = "±", [Keys.UrineBlood] = "−", [Keys.UrineUrobil] = "正常",
        }),
    };
    public const string ExamDate = "2026-11-10";

    [Fact]
    public void テスト撮影用の入力シート3人分を作る()
    {
        var dir = Environment.GetEnvironmentVariable("KENSHIN_TESTSET_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var cfg = new AppConfig { BaseDir = TestEnv.CaseDir, ConfigDir = TestEnv.ConfigDir, DataDir = TestEnv.TempFile("-data"), NetworkGuard = false };
        cfg.Clinic.Name = "テストクリニック";
        var state = new AppState(cfg, Masters.Load(TestEnv.ConfigDir), Criteria.Load(TestEnv.ConfigDir), new NetworkGuard(false));
        ReportService.Init(cfg.BaseDir);
        var docs = new List<IDocument>();
        var expect = new System.Text.StringBuilder();
        expect.AppendLine("# テスト撮影用 3 人分の期待値（架空）\n");
        expect.AppendLine($"健診実施日・採取日：{ExamDate}。入力シートは白紙に手書き風で記入済み。採血の報告書は同じ 3 人分を 1/2・2/2 の 2 枚ずつ。\n");
        foreach (var (id, name, kana, sex, birth, sheet) in People)
        {
            var fill = new Dictionary<string, string>(sheet)
            {
                [SheetTemplate.KeyFalcoId] = id, [SheetTemplate.KeyDate] = ExamDate.Replace("-", ""), [SheetTemplate.KeyBirth] = birth.Replace("-", ""), [SheetTemplate.KeySex] = sex,
            };
            var doc = new InputSheetDocument(state, state.Masters, null, fill, handwriting: true);
            docs.Add(doc);
            var png = doc.GenerateImages(new ImageGenerationSettings { RasterDpi = 150 }).First();
            File.WriteAllBytes(Path.Combine(dir, $"入力シート_{id}_{name.Replace(" ", "")}.png"), png);
            expect.AppendLine($"## {name}（{kana}）{sex} 生 {birth} 個人ID {id}\n");
            expect.AppendLine("| 項目 | 値 |\n|---|---|");
            foreach (var (k, v) in sheet) expect.AppendLine($"| {Lab.SheetReader.LabelOf(k)} | {v} |");
            expect.AppendLine();
        }
        File.WriteAllBytes(Path.Combine(dir, "入力シート_テスト3人.pdf"), Document.Merge(docs.ToArray()).GeneratePdf());
        File.WriteAllBytes(Path.Combine(dir, "入力シート_白紙.pdf"), new InputSheetDocument(state, state.Masters, null).GeneratePdf());
        File.WriteAllText(Path.Combine(dir, "期待値_入力シート.md"), expect.ToString());
    }
}
