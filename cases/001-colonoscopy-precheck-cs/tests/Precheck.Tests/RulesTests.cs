using Precheck;
using Xunit;

public class RulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    private static Reservation R(string status, int y, int m, int d, string menu, int hh = 14) =>
        new(status, new DateTime(y, m, d, hh, 0, 0), "", menu);

    private static ScheduleRow Row(DateOnly day, int hh, string menu, string name, string cardNo, string birth, string status = "予約") =>
        new(day, new DateTime(day.Year, day.Month, day.Day, hh, 0, 0), status, menu, name, "", cardNo, birth, "C1");

    [Fact]
    public void IsExam_MatchesColonoscopyMenusOnly()
    {
        var rules = new RulesConfig();
        Assert.True(Rules.IsExam(R("予約", 2026, 10, 31, "胃＋大腸カメラ検査"), rules));
        Assert.True(Rules.IsExam(R("予約", 2026, 10, 31, "大腸カメラ検査"), rules));
        Assert.False(Rules.IsExam(R("予約", 2026, 10, 31, "胃カメラ検査"), rules));
        Assert.False(Rules.IsExam(R("予約", 2026, 10, 31, "連鎖用(胃+大腸)"), rules));
        Assert.False(Rules.IsExam(R("予約", 2026, 10, 9, "大腸カメラ事前診察"), rules));
        Assert.False(Rules.IsExam(R("患者都合キャンセル", 2026, 10, 24, "胃＋大腸カメラ検査"), rules));
    }

    [Fact]
    public void HasPreExamFor_RespectsWindow()
    {
        var rules = new RulesConfig { WindowDays = 30 };
        var exam = R("予約", 2026, 10, 31, "胃＋大腸カメラ検査");
        Assert.True(Rules.HasPreExamFor(exam, new[] { R("予約", 2026, 10, 9, "大腸カメラ事前診察") }, rules));
        Assert.False(Rules.HasPreExamFor(exam, new[] { R("来院", 2026, 9, 1, "大腸カメラ事前診察") }, rules));
        Assert.False(Rules.HasPreExamFor(exam, new[] { R("予約", 2026, 11, 2, "大腸カメラ事前診察") }, rules));
        Assert.False(Rules.HasPreExamFor(exam, new[] { R("患者都合キャンセル", 2026, 10, 20, "大腸カメラ事前診察") }, rules));
    }

    [Fact]
    public void FindMissingPreExamFromSchedule_MatchesRegisteredAndNewPatients()
    {
        var exams = new[]
        {
            Row(new(2026, 10, 31), 13, "胃+大腸カメラ検査", "テスト 太郎", "535", "S49.08.12"),   // 事前診察あり（番号で突合）
            Row(new(2026, 10, 31), 14, "連鎖用(胃+大腸)", "テスト 太郎", "535", "S49.08.12"),     // 連鎖用は無視
            Row(new(2026, 11, 2), 13, "大腸カメラ検査", "新規 次郎", "", "R01.02.03"),            // 初診・事前診察あり（氏名+生年月日）
            Row(new(2026, 11, 5), 13, "大腸カメラ検査", "新規 三郎", "", "H10.05.05"),            // 初診・事前診察なし → 検出
            Row(new(2026, 11, 5), 14, "胃カメラ検査", "テスト 花子", "12", "S40.01.01"),          // 大腸ではない
            Row(new(2026, 9, 1), 13, "大腸カメラ検査", "過去 四郎", "99", "S30.01.01"),           // 過去 → 無視
        };
        var pres = new[]
        {
            Row(new(2026, 10, 9), 11, "大腸カメラ事前診察", "テスト 太郎", "535", "S49.08.12"),
            Row(new(2026, 10, 20), 11, "大腸カメラ事前診察", "新規 次郎", "", "R01.02.03"),
            Row(new(2026, 10, 20), 11, "大腸カメラ事前診察", "新規 三郎", "", "H99.99.99"),       // 生年月日が違う別人
        };
        var found = Rules.FindMissingPreExamFromSchedule(exams, pres, new RulesConfig(), Today);
        Assert.Equal(new[] { ("新規 三郎", "(未登録)") }, found.Select(f => (f.Patient.Name, f.Patient.CardNo)).ToArray());
        Assert.Equal("nm:新規 三郎|H10.05.05:2026-11-05", found[0].Key);
    }

    [Fact]
    public void FlagNeedsCheck_MarksNoRecentVisitAndUnregistered()
    {
        var exam = R("予約", 2026, 10, 31, "大腸カメラ検査", 13);
        var findings = new[]
        {
            new Finding(new Patient("535", "テスト 太郎", Key: "no:535"), exam),                                  // 20日前に外来再診あり → 通常
            new Finding(new Patient("12", "テスト 花子", Key: "no:12"), exam),                                    // 外来受診なし
            new Finding(new Patient("(未登録)", "新規 次郎", "R01.02.03", "nm:新規 次郎|R01.02.03"), exam),       // 未登録
            new Finding(new Patient("77", "テスト 三郎", Key: "no:77"), exam),                                    // 40日前は古い
            new Finding(new Patient("88", "テスト 四郎", Key: "no:88"), exam),                                    // キャンセルだけ
        };
        var outpatient = new[]
        {
            Row(new(2026, 9, 18), 10, "外来診察再診", "テスト 太郎", "535", "S49.08.12", "来院"),
            Row(new(2026, 8, 29), 10, "外来診察再診", "テスト 三郎", "77", "S40.01.01", "来院"),
            Row(new(2026, 10, 1), 10, "外来診察再診", "テスト 四郎", "88", "S40.01.01", "患者都合キャンセル"),
        };
        var outList = Rules.FlagNeedsCheck(findings, outpatient, Today, 30);
        Assert.Equal(new[]
        {
            "",
            "過去30日に外来受診なし",
            "過去30日に外来受診なし、診察券番号が未登録",
            "過去30日に外来受診なし",
            "過去30日に外来受診なし",
        }, outList.Select(f => string.Join("、", f.CheckReasons)).ToArray());
    }
}

public class DockAndNewBookingTests
{
    private static readonly RulesConfig R = new();
    private static readonly DateOnly Today = new(2026, 10, 20);

    private static ScheduleRow Row(string day, string status, string menu, string card, DateTime? created = null, string name = "架空 太郎") =>
        new(DateOnly.Parse(day), DateOnly.Parse(day).ToDateTime(new TimeOnly(9, 0)), status, menu, name, "", card, "S50.01.01", "E1", created);

    [Fact]
    public void 人間ドックの大腸コースとハイグレード消化器ドックは検査として数える()
    {
        Assert.True(Rules.IsExam(Row("2026-11-01", "予約", "人間ドック + 大腸カメラコース", "1").ToReservation(), R));
        Assert.True(Rules.IsExam(Row("2026-11-01", "予約", "人間ドック + 胃カメラ・大腸カメラコース", "1").ToReservation(), R));
        Assert.True(Rules.IsExam(Row("2026-11-01", "予約", "ハイグレード消化器ドック", "1").ToReservation(), R));
        Assert.False(Rules.IsExam(Row("2026-11-01", "予約", "人間ドック + 胃カメラコース", "1").ToReservation(), R));
        Assert.False(Rules.IsExam(Row("2026-11-01", "予約", "連鎖(人間ドック)", "1").ToReservation(), R));
        Assert.False(Rules.IsExam(Row("2026-11-01", "予約", "連鎖用(胃+大腸)", "1").ToReservation(), R));
    }

    [Fact]
    public void 直近30日に入った胃カメラコースの予約だけを拾う()
    {
        var rows = new List<ScheduleRow>
        {
            Row("2026-12-15", "予約", "人間ドック + 胃カメラコース", "1", new DateTime(2026, 10, 1)),   // 新しい
            Row("2026-12-16", "予約", "人間ドック+胃カメラコース", "2", new DateTime(2026, 10, 19)),    // 空白の違いは無視
            Row("2026-12-17", "予約", "人間ドック + 胃カメラコース", "3", new DateTime(2026, 9, 1)),    // 古い
            Row("2026-12-18", "キャンセル", "人間ドック + 胃カメラコース", "4", new DateTime(2026, 10, 10)),
            Row("2026-12-19", "予約", "人間ドック + 胃カメラ・大腸カメラコース", "5", new DateTime(2026, 10, 10)), // 別メニュー
            Row("2026-12-20", "予約", "人間ドック + 胃カメラコース", "6", null),                      // 入った日が不明
            Row("2026-10-01", "予約", "人間ドック + 胃カメラコース", "7", new DateTime(2026, 9, 25)),   // 過去の検査日
        };
        var found = Rules.FindNewBookings(rows, R, Today);
        Assert.Equal(new[] { "1", "2" }, found.Select(b => b.Patient.CardNo).ToArray());
        Assert.Equal("new:no:1:2026-12-15", found[0].Key);
    }

    [Fact]
    public void 要チェックの欄にも予約メニューが出る()
    {
        var f = new Finding(new Patient("(未登録)", "架空 花子", "S49.05.27", "nm:架空 花子|S49.05.27"),
            new Reservation("予約", new DateTime(2026, 11, 26, 11, 0, 0), "", "人間ドック + 大腸カメラコース"),
            new[] { "過去30日に外来受診なし", "診察券番号が未登録" });
        var text = Notify.BuildReport(new[] { f }, Today);
        var check = text[text.IndexOf("※要チェック※")..];
        Assert.Contains("予約メニュー", check);
        Assert.Contains("人間ドック + 大腸カメラコース", check);
        Assert.Contains("過去30日に外来受診なし、診察券番号が未登録", check);
    }

    [Fact]
    public void 結果ファイルに新しい予約の欄が付く()
    {
        var b = new NewBooking(new Patient("12345", "架空 太郎"), new Reservation("予約", new DateTime(2026, 12, 15, 9, 0, 0), "", "人間ドック + 胃カメラコース"), new DateTime(2026, 10, 1));
        var text = Notify.BuildReport(Array.Empty<Finding>(), Today, new[] { b }, "人間ドック + 胃カメラコース", 30, true);
        Assert.Contains("人間ドック + 胃カメラコース の新しい予約（直近 30 日に入ったもの、1 名）", text);
        Assert.Contains("12345", text);
        Assert.Contains("10/01", text);
        var none = Notify.BuildReport(Array.Empty<Finding>(), Today, Array.Empty<NewBooking>(), "人間ドック + 胃カメラコース", 30, false);
        Assert.Contains("判定できませんでした", none);
    }
}
