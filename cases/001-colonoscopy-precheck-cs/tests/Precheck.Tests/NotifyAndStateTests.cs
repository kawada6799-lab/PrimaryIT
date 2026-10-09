using Precheck;
using Xunit;

public class NotifyAndStateTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    [Fact]
    public void BuildReport_ListsPatientsSortedByExamDate_AndFlaggedAtBottom()
    {
        var fs = new List<Finding>
        {
            new(new Patient("535", "テスト 太郎"), new Reservation("予約", new DateTime(2026, 11, 2, 13, 30, 0), "", "胃＋大腸カメラ検査")),
            new(new Patient("12", "テスト 花子"), new Reservation("予約", new DateTime(2026, 10, 31, 13, 30, 0), "", "大腸カメラ検査")),
            new(new Patient("(未登録)", "新規 次郎", "R01.02.03"), new Reservation("予約", new DateTime(2026, 11, 5, 13, 30, 0), "", "大腸カメラ検査"),
                new[] { "過去30日に外来受診なし", "診察券番号が未登録" }),
        };
        var text = Notify.BuildReport(fs, Today);
        Assert.Contains("3 名", text);
        var lines = text.Split('\n');
        var i12 = Array.FindIndex(lines, l => l.StartsWith("12 "));
        var i535 = Array.FindIndex(lines, l => l.StartsWith("535"));
        Assert.True(i12 < i535, "検査日が早い方が先");
        Assert.Contains("10/31(土) 13:30", lines[i12]);
        var parts = text.Split("※要チェック※");
        Assert.Equal(2, parts.Length);
        Assert.Contains("新規 次郎（R01.02.03）", parts[1]);
        Assert.Contains("診察券番号が未登録", parts[1]);
        Assert.DoesNotContain("テスト 太郎", parts[1]);
    }

    [Fact]
    public void WriteReport_CreatesBomUtf8File()
    {
        var dir = Path.Combine(Path.GetTempPath(), "precheck-test-" + Guid.NewGuid().ToString("N"));
        var path = Notify.WriteReport("テスト\n", dir, Today);
        Assert.EndsWith("大腸カメラ事前診察なし_2026-10-08.txt", path);
        var raw = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, raw.Take(3).ToArray());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void State_Roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "precheck-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "s", "processed.json");
        var st = new State(path);
        Assert.False(st.IsNotified("no:1:2026-10-31"));
        st.MarkNotified("no:1:2026-10-31");
        st.Save();
        var st2 = new State(path);
        Assert.True(st2.IsNotified("no:1:2026-10-31"));
        Assert.False(st2.IsNotified("no:1:2026-11-01"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Config_LoadsJsonWithCommentsAndSnakeCase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "precheck-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, """
            {
              // コメントも書ける
              "wakumy": { "id": "x@example.com", "password": "pw", "browser": "msedge" },
              "rules": { "window_days": 45, "exam_keywords": ["大腸"] },
              "schedule": { "days_ahead": 10 },
              "notify": { "output_dir": "結果" },
              "state_path": "state/processed.json"
            }
            """);
        var cfg = Config.Load(path);
        Assert.Equal(45, cfg.Rules.WindowDays);
        Assert.Equal(10, cfg.Schedule.DaysAhead);
        Assert.Equal(30, cfg.Rules.RecentVisitDays); // 既定値
        Assert.Equal(Path.Combine(dir, "state", "processed.json"), cfg.StateFile);
        Directory.Delete(dir, true);
    }
}
