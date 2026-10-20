using Precheck;
using Xunit;

public class ApiDumpTests
{
    [Fact]
    public void 氏名や生年月日は伏せてステータスやメニューは見せる()
    {
        // 架空の値
        var json = """
        {"reservations":[{"id":12,"status":"予約","menu_name":"胃＋大腸カメラ検査","patient_name":"架空 太郎","birthday":"1970-01-01","card_no":"99999","start_at":"2026-10-20T09:00:00"}],"total":1}
        """;
        var d = ApiJsonSummary.Describe(json);
        Assert.DoesNotContain("架空 太郎", d);
        Assert.DoesNotContain("1970-01-01", d);
        Assert.DoesNotContain("99999", d);
        Assert.Contains("status: 文字列 = 予約", d);
        Assert.Contains("menu_name: 文字列 = 胃＋大腸カメラ検査", d);
        Assert.Contains("start_at: 文字列 = 2026-10-20T09:00:00", d);
        Assert.Contains("patient_name: 文字列 = <伏せ字", d);
        Assert.Contains("reservations: [ ] (1 件)", d);
    }

    [Fact]
    public void JSONでなければその旨を返す()
    {
        Assert.Equal("(JSON として読めない)", ApiJsonSummary.Describe("<html>"));
    }
}
