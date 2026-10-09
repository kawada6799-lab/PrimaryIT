using System.Diagnostics;
using System.Text;

namespace Precheck;

/// <summary>結果の知らせ方：テキストファイルを書いてメモ帳で開く。Windows の通知も出す。アカウント不要。</summary>
public static class Notify
{
    private const string Weekday = "月火水木金土日";

    private static string When(Reservation e) =>
        $"{e.Start:MM/dd}({Weekday[((int)e.Start.DayOfWeek + 6) % 7]}) {e.Start:HH:mm}";

    private static string DisplayName(Patient p) =>
        p.CardNo != "(未登録)" || p.Birth.Length == 0 ? p.Name : $"{p.Name}（{p.Birth}）";

    /// <summary>看護師がそのまま電話連絡に使える一覧。</summary>
    public static string BuildReport(IReadOnlyList<Finding> findings, DateOnly today)
    {
        var sb = new StringBuilder();
        var nl = "\n";
        sb.Append($"大腸カメラ事前診察の予約が無い患者  （{today:yyyy/MM/dd} 時点、{findings.Count} 名）{nl}{nl}");
        sb.Append($"診察券番号  氏名             検査日時            予約メニュー{nl}");
        sb.Append(new string('-', 70) + nl);
        foreach (var f in findings.OrderBy(f => f.Exam.Start))
            sb.Append($"{f.Patient.CardNo,-10}  {DisplayName(f.Patient),-14}  {When(f.Exam),-18}  {f.Exam.Menu}{nl}");
        sb.Append(nl + "Wakumy の患者管理で診察券番号を検索し、事前診察の予約を取ってもらうよう連絡してください。" + nl);

        var flagged = findings.Where(f => f.CheckReasons.Count > 0).OrderBy(f => f.Exam.Start).ToList();
        if (flagged.Count > 0)
        {
            sb.Append(nl + new string('=', 70) + nl);
            sb.Append($"※要チェック※（{flagged.Count} 名）  過去1か月に外来受診が無い、または診察券番号が未登録の方{nl}");
            sb.Append(new string('=', 70) + nl);
            sb.Append($"診察券番号  氏名             検査日時            理由{nl}");
            sb.Append(new string('-', 70) + nl);
            foreach (var f in flagged)
                sb.Append($"{f.Patient.CardNo,-10}  {DisplayName(f.Patient),-14}  {When(f.Exam),-18}  {string.Join("、", f.CheckReasons)}{nl}");
        }
        sb.Append(nl + "（precheck が自動作成。AI は使っていません）" + nl);
        return sb.ToString();
    }

    public static string WriteReport(string text, string outputDir, DateOnly today)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"大腸カメラ事前診察なし_{today:yyyy-MM-dd}.txt");
        // メモ帳で文字化けしないよう BOM 付き UTF-8。同じ日に複数回動いたら上書き
        File.WriteAllText(path, text.Replace("\n", "\r\n"), new UTF8Encoding(true));
        return path;
    }

    public static void OpenFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* 開けなくても本処理は続ける */ }
    }

    /// <summary>Windows の通知（トースト）。PowerShell 標準機能だけを使う。失敗しても無視。</summary>
    public static void Toast(string title, string message)
    {
        if (!OperatingSystem.IsWindows()) return;
        static string Ps(string s) => s.Replace("'", "''");
        var script =
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null;" +
            "$t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
            "$n = $t.GetElementsByTagName('text');" +
            $"$n.Item(0).AppendChild($t.CreateTextNode('{Ps(title)}')) | Out-Null;" +
            $"$n.Item(1).AppendChild($t.CreateTextNode('{Ps(message)}')) | Out-Null;" +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('precheck').Show([Windows.UI.Notifications.ToastNotification]::new($t))";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            p?.WaitForExit(15000);
        }
        catch { }
    }
}
