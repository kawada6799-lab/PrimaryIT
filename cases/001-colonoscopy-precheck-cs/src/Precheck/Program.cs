using System.Text;

namespace Precheck;

public static class Program
{
    private const string Usage = """
        precheck.exe [--config config.json] [--dry-run] [--headed] [-v]
        precheck.exe --check            動作確認モード（ブラウザ表示、ファイルは作らない、何も記録しない）
        precheck.exe --save-password    Wakumy のパスワードを入力して local\wakumy_password.txt に保存（初回だけ）
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var configPath = "config.json";
        bool dryRun = false, headed = false, check = false, savePassword = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": configPath = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                case "--headed": headed = true; break;
                case "--check": check = true; break;
                case "--save-password": savePassword = true; break;
                case "-v": case "--verbose": Log.Verbose = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default: Console.WriteLine($"不明な引数: {args[i]}\n{Usage}"); return 1;
            }
        }
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";

        if (savePassword)
        {
            var pw = ReadPassword("Wakumy のパスワード（画面には表示されません）: ");
            if (pw.Trim().Length == 0) { Console.WriteLine("空なので保存しませんでした。"); return 1; }
            var saved = Config.SavePassword(baseDir, pw);
            Console.WriteLine($"保存しました: {saved}（このファイルは配布物に含めないでください）");
            return 0;
        }

        if (check)
        {
            Console.WriteLine("動作確認（ファイルは作らず、何も記録しません）");
            Console.WriteLine("Enter を押すとブラウザが開き、予約一覧（内視鏡タブ）を日付ごとに読む本番と同じ流れで動きます。");
            Console.Write("Enter: ");
            Console.ReadLine();
            headed = true; dryRun = true; Log.Verbose = true;
            Console.WriteLine("ブラウザが開きます。自動で進むので、触らずに待ってください。");
        }

        Config cfg;
        try { cfg = Config.Load(configPath); }
        catch (ConfigError e) { Log.Error(e.Message); return 2; }
        catch (FileNotFoundException) { Log.Error($"設定ファイルが見つかりません: {configPath}"); return 2; }
        if (headed) cfg.Wakumy.Headless = false;

        var state = new State(cfg.StateFile);
        var today = DateOnly.FromDateTime(DateTime.Today);

        List<Finding> findings;
        try
        {
            await using var w = new Wakumy(cfg.Wakumy) { DebugDir = Path.GetDirectoryName(cfg.StateFile) };
            await w.StartAsync();
            await w.LoginAsync();
            findings = await ScanScheduleAsync(w, cfg, today);
        }
        catch (WakumyError e)
        {
            Log.Error(e.Message);
            return 2;
        }

        var newFindings = findings.Where(f => !state.IsNotified(f.Key)).ToList();
        foreach (var f in findings.Where(f => state.IsNotified(f.Key)))
            Log.Info($"通知済みのためスキップ: {f.Key}");

        var report = Notify.BuildReport(newFindings, today);
        if (dryRun)
        {
            Console.WriteLine("----- 結果（ファイルには書かない） -----");
            Console.Write(report);
        }
        else if (newFindings.Count > 0 || cfg.Notify.WriteEmpty)
        {
            var path = Notify.WriteReport(report, cfg.OutputDirFull, today);
            Log.Info($"結果ファイル: {path}");
            if (newFindings.Count > 0 && cfg.Notify.Toast)
                Notify.Toast("大腸カメラ事前診察チェック", $"事前診察なし {newFindings.Count} 名。結果ファイルを確認してください。");
            if (newFindings.Count > 0 && cfg.Notify.OpenAfter)
                Notify.OpenFile(path);
        }

        if (!dryRun)
        {
            foreach (var f in newFindings) state.MarkNotified(f.Key);
            state.Save();
        }
        Log.Info($"完了: 事前診察なし {findings.Count} 件（新規 {newFindings.Count} 件）");
        return 0;
    }

    /// <summary>予約一覧（内視鏡タブ）を日付ごとに見て検査予約を集め、外来診察タブの事前診察と突き合わせる。</summary>
    private static async Task<List<Finding>> ScanScheduleAsync(Wakumy w, Config cfg, DateOnly today)
    {
        var sc = cfg.Schedule;
        var examDays = Enumerable.Range(0, sc.DaysAhead + 1).Select(i => today.AddDays(i)).ToList();
        var preDays = Enumerable.Range(1, sc.DaysBack).Select(i => today.AddDays(-(sc.DaysBack - i + 1))).Concat(examDays).ToList();

        var examRows = await ScanTabAsync(w, sc.ExamTab, examDays);
        var preRows = await ScanTabAsync(w, sc.PreExamTab, preDays);
        Log.Info($"{sc.ExamTab}: {examDays.Count} 日分で予約 {examRows.Count} 件、{sc.PreExamTab}: {preDays.Count} 日分で予約 {preRows.Count} 件");

        var findings = Rules.FindMissingPreExamFromSchedule(examRows, preRows, cfg.Rules, today);
        findings = Rules.FlagNeedsCheck(findings, preRows, today, cfg.Rules.RecentVisitDays);
        Log.Info($"今後の大腸検査 {Rules.CountUpcomingExams(examRows, cfg.Rules, today)} 件のうち、事前診察なし {findings.Count} 件");
        return findings;
    }

    /// <summary>1 つの診療科タブで、指定した日付を順に開いて予約行を集める。</summary>
    private static async Task<List<ScheduleRow>> ScanTabAsync(Wakumy w, string tab, List<DateOnly> days)
    {
        await w.OpenScheduleTabAsync(tab);
        var rows = new List<ScheduleRow>();
        var failures = 0;
        foreach (var d in days)
        {
            try
            {
                await w.GotoDayAsync(d);
                failures = 0;
            }
            catch (WakumyError e)
            {
                failures++;
                Log.Warn($"{tab} {d}: {e.Message}");
                if (failures >= 3)
                    throw new WakumyError($"{tab} タブで日付移動が 3 回続けて失敗したため中断します。state\\debug_*.png を確認してください。");
                continue;
            }
            var dayRows = await w.ReadDayViewAsync(d, tab);
            rows.AddRange(dayRows);
            if (dayRows.Count > 0)
            {
                Log.Info($"{tab} {d:MM/dd}: 予約 {dayRows.Count} 件");
                foreach (var r in dayRows)
                    Log.Debug($"  {r.Status} {r.Start:HH:mm} {r.Menu} | {r.Name} | {(r.CardNo.Length == 0 ? "(未登録)" : r.CardNo)} {r.Birth}");
            }
        }
        return rows;
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
    }
}
