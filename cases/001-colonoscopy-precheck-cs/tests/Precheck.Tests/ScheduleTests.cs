using Precheck;
using Xunit;

public class ScheduleTests
{
    private static readonly DateOnly Day = new(2026, 10, 16);

    // 内視鏡タブ・日表示の innerText を模したもの（氏名・番号は架空）
    private const string Sample = """
        2026年10月16日(金)
        + 時間帯枠追加
        ステータス
        来院時刻(待ち)
        予約番号
        診察券番号
        生年月日(年齢)
        患者名
        予約メニュー
        予約/患者メモ
        問診メモ
        13:00 - 13:30
        0 / 2 枠 （WEB 0/2）
        + 新規予約追加
        まだ予約はありません
        13:30 - 14:00
        1 / 2 枠 （WEB 0/2）
        + 新規予約追加
        予約
        E8
        1234
        S50.01.01
        (51歳)
        テスト タロウ
        テスト 太郎 ♂
        連鎖(人間ドック)
        メンズ
        9/24→10/16へ変更 本…
        14:30 - 15:00
        2 / 2 枠 （WEB 0/2）
        + 新規予約追加
        予約
        C21
        5678
        S45.02.02
        (56歳)
        テスト ハナコ
        テスト 花子 ♀
        胃＋大腸カメラ検査
        鎮静 胃がん、肺がん検…
        問診依頼
        予約
        C22
        R01.02.03
        (7歳)
        シンキ ジロウ
        新規 次郎
        大腸カメラ検査
        15:00 - 15:30
        1 / 2 枠 （WEB 0/2）
        + 新規予約追加
        予約
        C21
        5678
        S45.02.02
        (56歳)
        テスト ハナコ
        テスト 花子 ♀
        連鎖用(胃+大腸)
        鎮静 胃がん、肺がん検…
        """;

    [Fact]
    public void ParseHeaderDate_Works() =>
        Assert.Equal(new DateOnly(2026, 10, 16), TextUtil.ParseHeaderDate("2026年10月16日(金)"));

    [Fact]
    public void ParseDayView_ReadsRows()
    {
        var rows = Schedule.ParseDayView(Sample, Day);
        Assert.Equal(new[] { "連鎖(人間ドック)", "胃+大腸カメラ検査", "大腸カメラ検査", "連鎖用(胃+大腸)" }, rows.Select(r => r.Menu).ToArray());
        var r0 = rows[0]; var r1 = rows[1]; var r2 = rows[2]; var r3 = rows[3];
        Assert.Equal(new DateTime(2026, 10, 16, 13, 30, 0), r0.Start);
        Assert.Equal(("1234", "S50.01.01", "テスト 太郎", "テスト タロウ", "E8"), (r0.CardNo, r0.Birth, r0.Name, r0.Kana, r0.ReservationNo));
        Assert.Equal(new DateTime(2026, 10, 16, 14, 30, 0), r1.Start);
        Assert.Equal("テスト 花子", r1.Name);
        // 初診で診察券番号が無い患者：氏名＋生年月日がキーになる
        Assert.Equal("", r2.CardNo);
        Assert.Equal("nm:新規 次郎|R01.02.03", r2.PatientKey);
        Assert.Equal("no:5678", r1.PatientKey);
        Assert.Equal("no:5678", r3.PatientKey);
        Assert.All(rows, r => Assert.Equal("予約", r.Status));
    }

    [Fact]
    public void ParseDayView_WebBadgeDoesNotShiftNameAndMenu()
    {
        const string text = """
            2026年10月10日(土)
            10:00 - 10:30
            1 / 2 枠 （WEB 1/2）
            + 新規予約追加
            予約
            WEB
            C3
            7777
            S59.01.01
            (42歳)
            テスト マサル
            テスト 勝 ♂
            胃カメラ検査
            予約
            C4
            R01.02.03
            (7歳)
            シンキ ジロウ
            新規 次郎
            WEB
            大腸カメラ検査
            """;
        var rows = Schedule.ParseDayView(text, new DateOnly(2026, 10, 10));
        Assert.Equal(new[] { ("テスト 勝", "胃カメラ検査", "7777"), ("新規 次郎", "大腸カメラ検査", "") },
            rows.Select(r => (r.Name, r.Menu, r.CardNo)).ToArray());
    }

    [Fact]
    public void HasSlots_DistinguishesClosedDays()
    {
        Assert.True(Schedule.HasSlots(Sample));
        Assert.False(Schedule.HasSlots("2026年10月11日(日)\n+ 時間帯枠追加\nステータス"));
    }
}
