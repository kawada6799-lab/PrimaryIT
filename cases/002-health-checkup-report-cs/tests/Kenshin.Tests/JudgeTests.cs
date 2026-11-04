using Kenshin.Domain;
using Kenshin.Judge;
using Xunit;

namespace Kenshin.Tests;

public class JudgeTests
{
    static ExamContext Ctx(string sex = "男", Dictionary<string, string>? values = null, List<Finding>? findings = null, List<BloodResult>? blood = null, List<Judgment>? existing = null) => new()
    {
        Patient = new Patient { Id = 1, Name = "健診 太郎", Sex = sex, BirthDate = "1974-05-01" },
        Exam = new Exam { Id = 10, PatientId = 1, ExamDate = "2026-11-04", Course = "企業健診" },
        Values = values ?? new(),
        Findings = findings ?? new(),
        Blood = blood ?? new(),
        Existing = existing ?? new(),
    };
    static JudgeEngine Engine() => new(TestEnv.Criteria(), TestEnv.Masters());

    [Fact]
    public void Bmi_計算()
    {
        Assert.Equal(25.7, JudgeEngine.Bmi(170.2, 74.5));
        Assert.Null(JudgeEngine.Bmi(null, 60));
        Assert.Equal(63.7, Engine().StandardWeight(170.2));
    }

    [Theory]
    [InlineData("170.2", "74.5", "", "180")] // BMI 25.7
    [InlineData("160.2", "48.1", "73.0", "150")] // BMI 18.7、女性腹囲 73
    [InlineData("160.0", "46.0", "", "160")] // BMI 18.0（仮：有所見健康）
    [InlineData("170.0", "60.0", "86.0", "180")] // BMI 20.8 だが男性腹囲 86
    public void 計測の判定(string h, string w, string waist, string expected)
    {
        var sex = h == "160.2" ? "女" : "男";
        var r = Engine().JudgeMeasure(Ctx(sex, new() { [Keys.Height] = h, [Keys.Weight] = w, [Keys.Waist] = waist }));
        Assert.Equal(expected, r.Code);
    }

    [Theory]
    [InlineData("120", "80", "", "", "150")]
    [InlineData("142", "91", "138", "88", "180")] // 2 回目を使う
    [InlineData("150", "95", "", "", "190")]
    [InlineData("165", "100", "", "", "210")]
    public void 血圧の判定(string s1, string d1, string s2, string d2, string expected)
    {
        var r = Engine().JudgeBp(Ctx(values: new() { [Keys.Bp1Sys] = s1, [Keys.Bp1Dia] = d1, [Keys.Bp2Sys] = s2, [Keys.Bp2Dia] = d2 }));
        Assert.Equal(expected, r.Code);
    }

    [Fact]
    public void 尿の判定_蛋白プラスマイナスで要再検査()
    {
        var r = Engine().JudgeUrine(Ctx(values: new() { [Keys.UrineSugar] = "−", [Keys.UrineProtein] = "±", [Keys.UrineBlood] = "−", [Keys.UrineUrobil] = "正常" }));
        Assert.Equal("170", r.Code);
        var ok = Engine().JudgeUrine(Ctx(values: new() { [Keys.UrineSugar] = "−", [Keys.UrineProtein] = "−", [Keys.UrineBlood] = "−", [Keys.UrineUrobil] = "正常" }));
        Assert.Equal("150", ok.Code);
        Assert.Null(Engine().JudgeUrine(Ctx()).Code);
    }

    [Fact]
    public void 視力と聴力と便()
    {
        var e = Engine();
        Assert.Equal("150", e.JudgeVision(Ctx(values: new() { [Keys.VisionR] = "0.5", [Keys.VisionL] = "0.9" })).Code);
        Assert.Equal("160", e.JudgeVision(Ctx(values: new() { [Keys.VisionR] = "0.3", [Keys.VisionL] = "0.9" })).Code);
        Assert.Equal("150", e.JudgeVision(Ctx(values: new() { [Keys.VisionR] = "0.3", [Keys.VisionRc] = "1.0", [Keys.VisionL] = "0.9" })).Code); // 矯正で判定
        Assert.Equal("180", e.JudgeHearing(Ctx(values: new() { [Keys.HearR1000] = "なし", [Keys.HearR4000] = "あり", [Keys.HearL1000] = "なし", [Keys.HearL4000] = "なし" })).Code);
        Assert.Equal("200", e.JudgeStool(Ctx(values: new() { [Keys.Stool1] = "−", [Keys.Stool2] = "+" })).Code);
    }

    [Fact]
    public void 所見から判定_胃内視鏡()
    {
        var f = new List<Finding> { new() { Section = Sections.Gastroscopy, Code = "V014", Text = "胃ポリープ" }, new() { Section = Sections.Gastroscopy, Code = "V016", Text = "逆流性食道炎" } };
        var r = Engine().JudgeFindingSection(Ctx(findings: f), Sections.Gastroscopy);
        Assert.Equal("180", r.Code);
        var none = Engine().JudgeFindingSection(Ctx(findings: new() { new() { Section = Sections.ChestXray, Code = "X001", Text = "異常を認めず" } }), Sections.ChestXray);
        Assert.Equal("150", none.Code);
    }

    [Fact]
    public void 血液の分野判定と重症閾値()
    {
        var blood = new List<BloodResult>
        {
            new() { Item = "AST (GOT)", Value = "27", RefLow = 8, RefHigh = 38 },
            new() { Item = "ALT (GPT)", Value = "48", RefLow = 4, RefHigh = 43, Flag = "↑" },
            new() { Item = "総コレステロール", Value = "224", RefLow = 130, RefHigh = 219 },
            new() { Item = "HbA1c NGSP", Value = "8.4", RefLow = 4.6, RefHigh = 6.2, Flag = "↑" },
            new() { Item = "クレアチニン", Value = "0.88", RefLow = 0.61, RefHigh = 1.04 },
            new() { Item = "CRP定量", Value = "0.05以下", RefText = "0.30以下", RefHigh = 0.30 },
        };
        var rs = Engine().JudgeBlood(Ctx(blood: blood)).ToDictionary(r => r.Section, r => r.Code);
        Assert.Equal("180", rs["血液:肝機能"]);
        Assert.Equal("180", rs["血液:脂質"]); // 基準値から外れを計算
        Assert.Equal("210", rs["血液:糖機能"]); // HbA1c 8.0 以上
        Assert.Equal("150", rs["血液:腎機能"]);
        Assert.Equal("150", rs["血液:血清学"]);
    }

    [Fact]
    public void メタボ判定()
    {
        var e = Engine();
        var v = new Dictionary<string, string> { [Keys.Waist] = "88.0", [Keys.Bp1Sys] = "142", [Keys.Bp1Dia] = "91", [Keys.Bp2Sys] = "138", [Keys.Bp2Dia] = "88" };
        var blood = new List<BloodResult> { new() { Item = "中性脂肪 (TG)", Value = "162" }, new() { Item = "HDLコレステロール", Value = "42" }, new() { Item = "血糖", Value = "104" }, new() { Item = "HbA1c NGSP", Value = "5.8" } };
        Assert.Equal("該当", e.JudgeMetabolic(Ctx(values: v, blood: blood)).Label);
        var v2 = new Dictionary<string, string>(v) { [Keys.Waist] = "73.0" };
        Assert.Equal("非該当", e.JudgeMetabolic(Ctx("女", v2, blood: blood)).Label);
        var blood2 = new List<BloodResult> { new() { Item = "中性脂肪 (TG)", Value = "100" }, new() { Item = "HDLコレステロール", Value = "60" }, new() { Item = "血糖", Value = "90" }, new() { Item = "HbA1c NGSP", Value = "5.2" } };
        Assert.Equal("予備群", e.JudgeMetabolic(Ctx(values: v, blood: blood2)).Label);
        Assert.Equal("判定不能", e.JudgeMetabolic(Ctx()).Label);
    }

    [Fact]
    public void 全体計算_手動は残り_総合は最も重い()
    {
        var existing = new List<Judgment> { new() { Section = Sections.Gastroscopy, Code = "200", Manual = true } };
        var v = new Dictionary<string, string> { [Keys.Height] = "170.2", [Keys.Weight] = "74.5", [Keys.Bp1Sys] = "120", [Keys.Bp1Dia] = "80" };
        var js = Engine().ComputeAll(Ctx(values: v, existing: existing), "2026-11-04 10:00:00");
        Assert.Equal("200", js.First(j => j.Section == Sections.Gastroscopy).Code);
        Assert.True(js.First(j => j.Section == Sections.Gastroscopy).Manual);
        Assert.Equal("180", js.First(j => j.Section == Sections.Measure).Code);
        Assert.Equal("200", js.First(j => j.Section == Sections.Overall).Code);
        Assert.DoesNotContain(js, j => j.Section == Sections.Urine); // 未入力は判定なし
    }

    [Fact]
    public void 判定コメントの提案()
    {
        var v = new Dictionary<string, string> { [Keys.Height] = "170.2", [Keys.Weight] = "74.5", [Keys.QSmoking] = "1" };
        var (h, c, cands) = Engine().SuggestComments(Ctx(values: v), "180");
        Assert.Equal("H403", h);
        Assert.Equal("C017", c); // 喫煙が先
        Assert.Contains("C018", cands);
        Assert.Contains("C006", cands);
        var (h2, c2, _) = Engine().SuggestComments(Ctx(), "150");
        Assert.Equal("H001", h2); Assert.Equal("C006", c2);
    }

    [Fact]
    public void 必須項目の漏れ()
    {
        var js = new List<Judgment> { new() { Section = Sections.Measure, Code = "150" }, new() { Section = "血液:肝機能", Code = "150" } };
        var missing = Engine().MissingRequired(Ctx(), js);
        Assert.Contains("視力", missing);
        Assert.DoesNotContain("血液検査", missing);
        Assert.DoesNotContain("計測", missing);
    }
}
