using System.Text;

namespace Precheck;

public static class Program
{
    private const string Usage = """
        precheck.exe [--config config.json] [--dry-run] [--headed] [-v]
        precheck.exe --check            動作確認モード（ブラウザ表示、ファイルは作らない、何も記録しない）
        precheck.exe --save-password    Wakumy のパスワードを入力して local\wakumy_password.txt に保存（初回だけ）
        precheck.exe --dump-api         調査モード：週表示を開いたときの通信を state\debug_api に保存（開発者に summary.txt だけ送る）
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var configPath = "config.json";
        bool dryRun = false, headed = false, check = false, savePassword = false, dumpApi = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": configPath = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                case "--headed": headed = true; break;
                case "--check": check = true; break;
                case "--save-password": savePassword = true; break;
                case "--dump-api": dumpApi = true; break;
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

        if (dumpApi) return await DumpApiAsync(cfg);

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

        // 同じ患者・同じ検査日は MaxTimes 回まで出す。2 回目以降は「n回目」と表示し、超えたら出さない
        var newFindings = new List<Finding>();
        foreach (var f in findings)
        {
            var n = state.NotifiedCount(f.Key);
            if (n >= cfg.Rules.MaxTimes) { Log.Info($"{n} 回知らせたのでスキップ: {f.Key}"); continue; }
            newFindings.Add(f with { Times = n + 1 });
        }

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

    /// <summary>調査モード：週表示・月表示を開いて、裏で届く JSON を保存する。判定や記録は何もしない。</summary>
    private static async Task<int> DumpApiAsync(Config cfg)
    {
        var stateDir = Path.GetDirectoryName(cfg.StateFile) ?? ".";
        var dir = Path.Combine(stateDir, "debug_api");
        Console.WriteLine("通信の調査モードです。ブラウザが開き、予約一覧の週表示と月表示を開いて、裏で届くデータを保存します。");
        Console.WriteLine($"保存先: {dir}");
        Console.WriteLine("  summary.txt         … 項目名と型だけ（氏名などは伏せ字）。これを開発者に送る");
        Console.WriteLine("  それ以外の .json/.txt/.png … 患者情報を含む。送らない");
        cfg.Wakumy.Headless = false;
        try
        {
            await using var w = new Wakumy(cfg.Wakumy) { DebugDir = dir };
            await w.StartAsync();
            await w.LoginAsync();
            var dump = new ApiDump(dir);
            w.AttachApiDump(dump);
            await w.ProbeWeekViewAsync(cfg.Schedule.ExamTab);
            await w.ProbeWeekViewAsync(cfg.Schedule.PreExamTab);
            var summary = await dump.SaveAsync();
            Log.Info($"保存しました: {summary}");
        }
        catch (WakumyError e)
        {
            Log.Error(e.Message);
            return 2;
        }
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
        Log.Info($"{tab}: {days.Count} 日分を {sw.Elapsed.TotalSeconds:F0} 秒で読みました（1 日あたり {sw.Elapsed.TotalSeconds / Math.Max(1, days.Count):F1} 秒）");
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
