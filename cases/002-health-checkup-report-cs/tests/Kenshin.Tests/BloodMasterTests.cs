using Kenshin.Config;
using Kenshin.Domain;
using Xunit;

namespace Kenshin.Tests;

/// <summary>血液項目マスター（config/血液項目.csv）。項目名の名寄せと、枠の補完。患者データはすべて架空。</summary>
public class BloodMasterTests
{
    [Fact]
    public void マスターはファルコ報告書の全項目を持つ()
    {
        var m = TestEnv.Masters();
        Assert.True(m.BloodItems.Count >= 46, "項目数 " + m.BloodItems.Count);
        Assert.Contains(m.BloodItems, b => b.Item == "総ビリルビン" && b.Category == "肝機能" && b.Unit == "mg/dL" && b.RefMale == "0.2~1.2");
        Assert.Contains(m.BloodItems, b => b.Item == "CA19-9" && b.Category == "腫瘍マーカー");
        // 並びは報告書の順（添字が連番）
        Assert.Equal(Enumerable.Range(0, m.BloodItems.Count), m.BloodItems.Select(b => b.Index));
        // 判定基準の分野名と一致する（分野ごとの判定に乗る）
        var c = TestEnv.Criteria();
        foreach (var b in m.BloodItems) Assert.Equal(b.Category, c.Blood.CategoryOf(b.Item));
    }

    [Theory]
    [InlineData("AST (GOT)", "AST（GOT）")]
    [InlineData("ＡＳＴ（ＧＯＴ）", "AST（GOT）")]
    [InlineData("GOT", "AST（GOT）")]
    [InlineData("γ-GT (γGTP)", "γ-GT（γGTP）")]
    [InlineData("LDLコレステロール 計算法", "LDLコレステロール（計算法）")]
    [InlineData("中性脂肪 (TG)", "中性脂肪（TG）")]
    [InlineData("HbA1c NGSP", "HbA1c（NGSP）")]
    [InlineData("S/CO", "HCV抗体 S/CO")]
    [InlineData("IU/mL", "HBs抗原 定量")]
    [InlineData("白血球数", "白血球数")]
    [InlineData("LD (IFCC)", "LD（IFCC）")]
    [InlineData("HBs抗原 精密測定", "HBs抗原（精密測定）")]
    public void 項目名をマスターに揃える(string imported, string canonical)
    {
        var m = TestEnv.Masters();
        Assert.Equal(canonical, m.MatchBloodItem(imported)?.Item);
    }

    [Fact]
    public void LDはLDLに当たらない()
    {
        var m = TestEnv.Masters();
        Assert.Equal("LD（IFCC）", m.MatchBloodItem("LD")?.Item);
        Assert.Equal("LDLコレステロール（計算法）", m.MatchBloodItem("LDL")?.Item);
        Assert.Null(m.MatchBloodItem("架空の項目"));
    }

    [Fact]
    public void 手入力は単位と基準値を性別で補う()
    {
        var m = TestEnv.Masters();
        var b = new BloodResult { Item = "ヘモグロビン", Value = "12.0" };
        m.Canonicalize(b, "女");
        Assert.Equal("ヘモグロビン量", b.Item); Assert.Equal("g/dL", b.Unit); Assert.Equal("11.3~15.2", b.RefText); Assert.Equal(11.3, b.RefLow); Assert.Equal("血液一般", b.Category);
        var b2 = new BloodResult { Item = "Hb", Value = "12.0", Unit = "g/dL", RefText = "13.5~17.6", RefLow = 13.5, RefHigh = 17.6 };
        m.Canonicalize(b2, "男");
        Assert.Equal("ヘモグロビン量", b2.Item); Assert.Equal("13.5~17.6", b2.RefText); // 取り込んだ基準値はそのまま
        var q = new BloodResult { Item = "HBs抗原 精密測定", Value = "陰性", RefText = "インセイ" };
        m.Canonicalize(q, "男");
        Assert.Equal("HBs抗原（精密測定）", q.Item); Assert.Equal("陰性", q.RefText); Assert.Null(q.RefHigh);
        var b3 = new BloodResult { Item = "架空の項目", Value = "1" };
        m.Canonicalize(b3, "男");
        Assert.Equal("架空の項目", b3.Item);
    }
}
