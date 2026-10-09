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
