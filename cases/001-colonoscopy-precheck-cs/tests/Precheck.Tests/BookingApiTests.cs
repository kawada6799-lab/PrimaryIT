using Precheck;
using Xunit;

public class BookingApiTests
{
    // 架空の値だけを使う
    private const string Sample = """
    {"data":[
      {"id":1,"bookingDate":"2026-11-05","dailyBookingTimeFrameStartTime":"13:00:00","status":"予約","assignedNumber":"E3",
       "nameSei":"架空","nameMei":"太郎","nameSeiKana":"カクウ","nameMeiKana":"タロウ","birthDate":"1975-01-01",
       "patientId":{"id":9,"registerId":"12345"},"bookingMenuId":{"id":"m1","name":"胃＋大腸カメラ検査"},"chainBookings":[]},
      {"id":2,"bookingDate":"2026-11-05","dailyBookingTimeFrameStartTime":"13:00:00","status":"予約","assignedNumber":"E4",
       "nameSei":"架空","nameMei":"花子","nameSeiKana":"","nameMeiKana":"","birthDate":"2001-03-04",
       "patientId":null,"bookingMenuId":{"id":"m2","name":"連鎖用(胃+大腸)"}},
      {"id":3,"bookingDate":"bad","status":"予約"},
      {"id":4,"bookingDate":"2026-11-06","dailyBookingTimeFrameStartTime":"09:00:00","status":"キャンセル","nameSei":"","nameMei":"","patientId":null,"bookingMenuId":null}
    ],"meta":{"filter_count":4}}
    """;

    [Fact]
    public void 予約データを行に変換できる()
    {
        var rows = BookingApi.ParseBookings(Sample);
        Assert.Equal(2, rows.Count);
        var a = rows[0];
        Assert.Equal(new DateOnly(2026, 11, 5), a.Day);
        Assert.Equal(13, a.Start.Hour);
        Assert.Equal("予約", a.Status);
        Assert.Equal("胃+大腸カメラ検査", a.Menu); // 全角＋は半角に寄せる（日表示の読み取りと同じ）
        Assert.Equal("架空 太郎", a.Name);
        Assert.Equal("12345", a.CardNo);
        Assert.Equal("S50.01.01", a.Birth);
        Assert.Equal("E3", a.ReservationNo);
        Assert.Equal("no:12345", a.PatientKey);
        var b = rows[1];
        Assert.Equal("", b.CardNo);
        Assert.Equal("H13.03.04", b.Birth);
        Assert.Equal("nm:架空 花子|H13.03.04", b.PatientKey);
    }

    [Fact]
    public void dataが無ければ例外()
    {
        Assert.Throws<FormatException>(() => BookingApi.ParseBookings("{\"x\":1}"));
    }

    [Fact]
    public void 日付範囲を書き換えられる()
    {
        var post = """{"query":{"filter":{"_and":[{},{"bookingDate":{"_between":["2026-10-18","2026-10-24"]}},{"status":{"_neq":"x"}}]},"limit":-1}}""";
        var s = BookingApi.RewriteDateRange(post, new DateOnly(2026, 9, 20), new DateOnly(2026, 12, 4));
        Assert.NotNull(s);
        Assert.Contains("\"_between\":[\"2026-09-20\",\"2026-12-04\"]", s);
        Assert.DoesNotContain("2026-10-18", s);
        Assert.Contains("\"_neq\":\"x\"", s);
    }

    [Fact]
    public void gte_lteの形も書き換えられる()
    {
        var post = """{"filter":{"date":{"_gte":"2026-10-18","_lte":"2026-10-24"}}}""";
        var s = BookingApi.RewriteDateRange(post, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Assert.Equal("""{"filter":{"date":{"_gte":"2026-01-01","_lte":"2026-01-31"}}}""", s);
    }

    [Fact]
    public void 日付範囲が無ければnull()
    {
        Assert.Null(BookingApi.RewriteDateRange("""{"filter":{"id":{"_eq":1}}}""", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2)));
        Assert.Null(BookingApi.RewriteDateRange("not json", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2)));
    }

    [Theory]
    [InlineData(1975, 1, 1, "S50.01.01")]
    [InlineData(1989, 1, 7, "S64.01.07")]
    [InlineData(1989, 1, 8, "H1.01.08")]
    [InlineData(2019, 5, 1, "R1.05.01")]
    [InlineData(1926, 12, 25, "S1.12.25")]
    public void 和暦に変換できる(int y, int m, int d, string expected)
    {
        Assert.Equal(expected, TextUtil.ToWareki(new DateOnly(y, m, d)));
    }
}
