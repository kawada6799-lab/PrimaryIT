using Kenshin.Lab;
using Kenshin.Web;
using Xunit;

namespace Kenshin.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("39 ~ 98", 39.0, 98.0)]
    [InlineData("427 ~570", 427.0, 570.0)]
    [InlineData("13.5~ 17.6", 13.5, 17.6)]
    [InlineData("2.5以下", null, 2.5)]
    [InlineData("60.0以上", 60.0, null)]
    [InlineData("1.0未満 陰性", null, 1.0)]
    [InlineData("0.05未満", null, 0.05)]
    [InlineData("", null, null)]
    [InlineData("(-)", null, null)]
    public void 基準値の読み取り(string text, double? lo, double? hi)
    {
        var (l, h) = FalcoPdfReader.ParseRange(text);
        Assert.Equal(lo, l); Assert.Equal(hi, h);
    }

    [Theory]
    [InlineData("白血球数 54 ×10² /μL 39 ~ 98", "白血球数", "54", "×10²/μL", "39 ~ 98", "")]
    [InlineData("HbA1c NGSP ↑ 8.4 % 4.6~6.2", "HbA1c NGSP", "8.4", "%", "4.6~6.2", "↑")]
    [InlineData("HbA1c NGSP 8.4 % 4.6~6.2", "HbA1c NGSP", "8.4", "%", "4.6~6.2", "↑")]
    [InlineData("S/CO 0.1以下 1.0未満 陰性", "S/CO", "0.1以下", "", "1.0未満 陰性", "")]
    [InlineData("CRP定量 0.05以下 mg/dL 0.30以下", "CRP定量", "0.05以下", "mg/dL", "0.30以下", "")]
    [InlineData("HBs抗原 精密測定 陰性 インセイ", "HBs抗原 精密測定", "陰性", "", "インセイ", "")]
    [InlineData("ヘモグロビン量 15.6 g/dL 13.5~ 17.6", "ヘモグロビン量", "15.6", "g/dL", "13.5~ 17.6", "")]
    [InlineData("eGFR 71.0 60.0以上", "eGFR", "71.0", "", "60.0以上", "")]
    [InlineData("ヘモグロビン量 10.1 g/dL 13.5~17.6", "ヘモグロビン量", "10.1", "g/dL", "13.5~17.6", "↓")]
    public void 項目行の読み取り(string line, string item, string value, string unit, string refText, string flag)
    {
        var r = FalcoPdfReader.ParseItemLine(line);
        Assert.NotNull(r);
        Assert.Equal(item, r!.Item); Assert.Equal(value, r.Value); Assert.Equal(unit, r.Unit.Replace(" ", "")); Assert.Equal(refText, r.RefText); Assert.Equal(flag, r.Flag);
    }

    [Theory]
    [InlineData("受付日 '26/10/19 報告日 '26/10/20 採取日 '26/10/19 12:47")]
    [InlineData("検査項目 検査結果 単位 基準値")]
    [InlineData("血液一般検査")]
    [InlineData("12:47")]
    public void 項目でない行は読まない(string line) => Assert.Null(FalcoPdfReader.ParseItemLine(line));

    [Fact]
    public void テキスト全体の読み取り_日付と項目()
    {
        var text = """
            検査報告書
            00-625-250(2/2)
            なごやおなか恒川クリニック 様
            個人ID 0001 健診 太郎 様
            受付日 '26/10/19 報告日 '26/10/20 採取日 '26/10/19 12:47
            検査項目 検査結果 単位 基準値
            血液一般検査
            白血球数 54 ×10² /μL 39 ~ 98
            赤血球数 512 ×10⁴ /μL 427 ~570
            HbA1c NGSP ↑ 8.4 % 4.6~6.2
            総コレステロール 151 mg/dL 130~219
            RPR 定性 (+) (-)
            """;
        var rep = FalcoPdfReader.ReadText(text);
        Assert.Equal("2026-10-19", rep.TakenAt);
        Assert.Equal("2026-10-20", rep.ReportedAt);
        Assert.Equal("00-625-250", rep.RequestNo);
        Assert.Equal("0001", rep.IdHint);
        Assert.Contains("健診 太郎", rep.NameHint);
        Assert.Equal(5, rep.Items.Count);
        Assert.Equal("↑", rep.Items.First(i => i.Item.StartsWith("HbA1c")).Flag);
    }

    [Theory]
    [InlineData("2026/11/04", "2026-11-04")]
    [InlineData("2026-1-4", "2026-01-04")]
    [InlineData("S52.10.7", "1977-10-07")]
    [InlineData("H2/9/24", "1990-09-24")]
    [InlineData("R5-1-1", "2023-01-01")]
    [InlineData("１９７４／５／１", "1974-05-01")]
    [InlineData("なし", "")]
    public void 日付の正規化(string input, string expected) => Assert.Equal(expected, Api.NormalizeDate(input));
}
