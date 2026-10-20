using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Precheck;

public sealed class WakumyError : Exception
{
    public WakumyError(string message) : base(message) { }
}

/// <summary>
/// Playwright で Wakumy 医療機関管理画面を操作する。
/// 画面の部品は「表示されている文字」で探す（class 名はビルドで変わるため）。
/// </summary>
public sealed class Wakumy : IAsyncDisposable
{
    private readonly WakumyConfig _cfg;
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IPage _page = null!;

    /// <summary>失敗時の画面保存先（null なら保存しない）。</summary>
    public string? DebugDir { get; set; }

    public Wakumy(WakumyConfig cfg) => _cfg = cfg;

    public async Task StartAsync()
    {
        _pw = await Playwright.CreateAsync();
        try
        {
            _browser = await _pw.Chromium.LaunchAsync(new() { Channel = _cfg.Browser, Headless = _cfg.Headless });
        }
        catch (PlaywrightException e) when (_cfg.Browser.Length > 0)
        {
            throw new WakumyError(
                $"ブラウザ「{_cfg.Browser}」を起動できませんでした。Windows 標準の Microsoft Edge が入っているか確認してください。（{e.Message.Split('\n')[0]}）");
        }
        var options = new BrowserNewContextOptions
        {
            Locale = "ja-JP",
            ViewportSize = new() { Width = 1400, Height = 900 },
        };
        var context = await _browser.NewContextAsync(options);
        if (_cfg.Headless)
        {
            // 非表示モードのブラウザは名乗り（User-Agent）に "Headless" が付く。
            // それを理由に画面やデータを返さないサービスがあるので、表示モードと同じ名乗りにする
            var probe = await context.NewPageAsync();
            var ua = await probe.EvaluateAsync<string>("navigator.userAgent");
            await context.CloseAsync();
            if (ua.Contains("Headless", StringComparison.OrdinalIgnoreCase))
            {
                options.UserAgent = ua.Replace("HeadlessEdg", "Edg").Replace("HeadlessChrome", "Chrome").Replace("Headless", "");
                Log.Debug($"User-Agent から Headless を外しました: {options.UserAgent}");
            }
            context = await _browser.NewContextAsync(options);
        }
        context.SetDefaultTimeout(_cfg.TimeoutMs);
        _page = await context.NewPageAsync();
        // 画面側のエラーを記録しておき、失敗したときに保存する（原因調査用）
        _page.Console += (_, m) => { if (m.Type is "error" or "warning") Remember($"console.{m.Type}: {m.Text}"); };
        _page.PageError += (_, e) => Remember($"pageerror: {e}");
        _page.RequestFailed += (_, r) => Remember($"requestfailed: {r.Method} {r.Url} {r.Failure}");
        _page.Response += (_, r) => { if (r.Status >= 400) Remember($"http {r.Status}: {r.Request.Method} {r.Url}"); };
    }

    private readonly List<string> _events = new();
    private void Remember(string line)
    {
        lock (_events)
        {
            if (_events.Count < 500) _events.Add($"{DateTime.Now:HH:mm:ss} {line}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _pw?.Dispose();
    }

    // ---------- ① ログイン ----------
    public async Task LoginAsync()
    {
        await _page.GotoAsync(_cfg.LoginUrl);
        await _page.GetByText("医療機関ログイン").First.WaitForAsync();
        await _page.Locator("input:not([type='password'])").First.FillAsync(_cfg.Id);
        await _page.Locator("input[type='password']").First.FillAsync(_cfg.Password);
        await _page.GetByRole(AriaRole.Button, new() { Name = "ログイン" }).ClickAsync();
        try
        {
            await HeaderNav("予約一覧").WaitForAsync();
        }
        catch (TimeoutException)
        {
            throw new WakumyError("ログイン後の画面が開きませんでした。ID/パスワードを確認してください。");
        }
    }

    // ---------- 予約データを期間ごと 1 回で受け取る（api モード） ----------
    /// <summary>
    /// 指定タブの予約を from〜to の範囲で一度に受け取る。
    /// 予約一覧で週表示を開くときに画面が送る問い合わせを捕まえ、日付範囲だけ書き換えて送り直す。
    /// 想定と違う形だったら null を返す（呼び出し側が日表示の読み取りに切り替える）。
    /// </summary>
    public async Task<List<ScheduleRow>?> ReadRangeViaApiAsync(string tab, DateOnly from, DateOnly to)
    {
        IRequest req;
        await OpenScheduleAsync(); // 開かなければ WakumyError（日表示に切り替えても同じなので、ここで止める）
        try
        {
            // 日表示にしてからタブを選び、「週表示」に切り替える瞬間の問い合わせを捕まえる（日→週で必ず送られる）
            var dayBtn = _page.GetByText("日表示", new() { Exact = true });
            if (await dayBtn.CountAsync() > 0) await dayBtn.First.ClickAsync();
            await _page.GetByText(tab, new() { Exact = true }).First.ClickAsync();
            await _page.WaitForTimeoutAsync(500);
            req = await _page.RunAndWaitForRequestAsync(
                async () => await _page.GetByText("週表示", new() { Exact = true }).First.ClickAsync(),
                r => r.Method == "POST" && r.Url.EndsWith(BookingApi.PathSuffix, StringComparison.Ordinal),
                new() { Timeout = 15000 });
        }
        catch (Exception e) when (e is TimeoutException or PlaywrightException)
        {
            Log.Warn($"{tab}: 予約データの問い合わせを捕まえられませんでした（{e.Message.Split('\n')[0]}）");
            await DebugShotAsync($"api_capture_failed_{tab}");
            return null;
        }

        var body = BookingApi.RewriteDateRange(req.PostData ?? "", from, to);
        if (body is null)
        {
            Log.Warn($"{tab}: 問い合わせの中に日付の範囲が見つかりませんでした");
            return null;
        }
        var headers = (await req.AllHeadersAsync())
            .Where(h => !h.Key.StartsWith(':') && !string.Equals(h.Key, "content-length", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value);
        try
        {
            var resp = await _page.Context.APIRequest.PostAsync(req.Url, new() { Headers = headers, DataString = body, Timeout = 60000 });
            if (!resp.Ok)
            {
                Log.Warn($"{tab}: 予約データの取得に失敗しました（HTTP {resp.Status}）");
                return null;
            }
            var rows = BookingApi.ParseBookings(await resp.TextAsync());
            var outside = rows.Count(r => r.Day < from || r.Day > to);
            if (outside > 0)
            {
                Log.Warn($"{tab}: 指定した期間の外の予約が {outside} 件届きました。範囲の指定が効いていないので日表示の読み取りに切り替えます");
                return null;
            }
            Log.Info($"{tab}: {from:MM/dd}〜{to:MM/dd} の予約 {rows.Count} 件を 1 回で受け取りました");
            return rows;
        }
        catch (Exception e) when (e is TimeoutException or PlaywrightException or FormatException or JsonException)
        {
            Log.Warn($"{tab}: 予約データを読めませんでした（{e.Message.Split('\n')[0]}）");
            return null;
        }
    }

    /// <summary>調査用：以後に届く JSON を保存する。</summary>
    public void AttachApiDump(ApiDump dump) => dump.Attach(_page);

    /// <summary>調査用：予約一覧の指定タブを開き、週表示に切り替え、翌週にも 1 回進めて通信を発生させる。</summary>
    public async Task ProbeWeekViewAsync(string tab)
    {
        await OpenScheduleTabAsync(tab);
        await _page.WaitForTimeoutAsync(1500);
        var weekBtn = _page.GetByText("週表示", new() { Exact = true }).First;
        await weekBtn.ClickAsync();
        await _page.WaitForTimeoutAsync(3000);
        await DebugShotAsync($"week_{tab}");
        DebugText($"week_{tab}", await _page.Locator("body").InnerTextAsync());
        // 翌週へ。ボタンの文言が分からないので「次の週」「次週」「>」の順に試す
        foreach (var label in new[] { "次の週", "次週", "翌週", ">" })
        {
            var b = _page.GetByText(label, new() { Exact = true });
            if (await b.CountAsync() > 0)
            {
                try { await b.First.ClickAsync(new() { Timeout = 3000 }); await _page.WaitForTimeoutAsync(3000); break; } catch { }
            }
        }
        // 月表示も 1 回見ておく（1 か月を 1 回で取れるかもしれない）
        var monthBtn = _page.GetByText("月表示", new() { Exact = true });
        if (await monthBtn.CountAsync() > 0)
        {
            try { await monthBtn.First.ClickAsync(new() { Timeout = 3000 }); await _page.WaitForTimeoutAsync(3000); await DebugShotAsync($"month_{tab}"); } catch { }
        }
    }

    /// <summary>ヘッダーの「予約一覧」を開き、日表示の画面（「時間帯枠追加」が出る）になるまで待つ。</summary>
    private async Task OpenScheduleAsync()
    {
        await HeaderNav("予約一覧").ClickAsync();
        try
        {
            await _page.GetByText("時間帯枠追加").First.WaitForAsync();
            return;
        }
        catch (TimeoutException)
        {
            // 内容が出ないまま時間切れ。一時的な不調のことがあるので、読み込み直して 1 回だけやり直す
            Log.Warn("予約一覧の内容が表示されないので、画面を読み込み直してもう一度開きます");
            await DebugShotAsync("schedule_blank_1st");
        }
        try
        {
            await _page.ReloadAsync();
            await HeaderNav("予約一覧").WaitForAsync();
            await HeaderNav("予約一覧").ClickAsync();
            await _page.GetByText("時間帯枠追加").First.WaitForAsync(new() { Timeout = _cfg.TimeoutMs * 2 });
        }
        catch (TimeoutException)
        {
            await DebugShotAsync("schedule_blank");
            throw new WakumyError(
                "予約一覧の画面が開きませんでした（内容が表示されないまま）。" +
                (_cfg.Headless ? "config.json の \"headless\" を false にして 手動実行.bat を試してください。" : "") +
                " state\\debug_schedule_blank.txt を開発者に見せてください。");
        }
    }

    private ILocator HeaderNav(string label) =>
        _page.GetByRole(AriaRole.Link, new() { Name = label })
             .Or(_page.GetByRole(AriaRole.Button, new() { Name = label })).First;

    // ---------- 予約一覧（日表示）を日付ごとに読む ----------
    /// <summary>ヘッダーの「予約一覧」→ 診療科タブ（内視鏡 / 外来診察 …）→ 日表示。</summary>
    public async Task OpenScheduleTabAsync(string tab)
    {
        await OpenScheduleAsync();
        await _page.GetByText(tab, new() { Exact = true }).First.ClickAsync();
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var dayBtn = _page.GetByText("日表示", new() { Exact = true });
        if (await dayBtn.CountAsync() > 0)
        {
            await dayBtn.First.ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
    }

    /// <summary>日表示の日付を d に合わせる。左のカレンダーで月を合わせて日を押し、見出しの日付で必ず確認する。</summary>
    public async Task GotoDayAsync(DateOnly d, int retry = 0)
    {
        if (await HeaderDateAsync() == d) return;
        var cal = await CalendarAsync();
        // 月を合わせる
        var ok = false;
        for (var i = 0; i < 36; i++)
        {
            var shown = await CalendarMonthAsync(cal);
            if (shown is null)
            {
                await DebugShotAsync("calendar_unreadable");
                throw new WakumyError("カレンダーの年月が読めませんでした");
            }
            if (shown == (d.Year, d.Month)) { ok = true; break; }
            var forward = (d.Year, d.Month).CompareTo(shown.Value) > 0;
            await cal.GetByText(forward ? "次の月" : "前の月", new() { Exact = true }).First.ClickAsync();
            await _page.WaitForTimeoutAsync(400);
        }
        if (!ok) throw new WakumyError($"カレンダーを {d:yyyy-MM} に合わせられませんでした");

        // 日を押す。前月末・翌月初の薄い日付と同じ数字が並ぶことがあるので、押したあと見出しで確認し、違えば別の候補
        var cells = cal.GetByText($"{d.Day:00}", new() { Exact = true });
        if (await cells.CountAsync() == 0) cells = cal.GetByText(d.Day.ToString(), new() { Exact = true });
        var n = await cells.CountAsync();
        if (n == 0)
        {
            await DebugShotAsync("calendar_day_missing");
            throw new WakumyError($"カレンダーに {d} の日付が見つかりません");
        }
        var order = d.Day < 15 ? Enumerable.Range(0, n) : Enumerable.Range(0, n).Reverse();
        foreach (var idx in order)
        {
            await cells.Nth(idx).ClickAsync();
            if (await WaitForHeaderAsync(d))
            {
                Log.Debug($"日付移動: 目標 {d} → 表示 {d}");
                return;
            }
            Log.Debug($"日付移動: 目標 {d} → 表示 {await HeaderDateAsync()}（候補 {idx}）");
            cal = await CalendarAsync();
            if (await CalendarMonthAsync(cal) != (d.Year, d.Month))
            {
                // 薄い日付を押して月が変わってしまった。月を戻してやり直す（1 回だけ）
                if (retry < 1) { await GotoDayAsync(d, retry + 1); return; }
                break;
            }
        }
        await DebugShotAsync($"goto_day_failed_{d:yyyy-MM-dd}");
        throw new WakumyError($"日付を {d} に移動できませんでした（表示は {await HeaderDateAsync()}）");
    }

    /// <summary>左のカレンダー全体（前の月／今日／次の月、年月、日付の数字を含む要素）。</summary>
    private async Task<ILocator> CalendarAsync()
    {
        var node = _page.GetByText("前の月", new() { Exact = true }).First;
        for (var i = 0; i < 8; i++)
        {
            node = node.Locator("xpath=..");
            var text = TextUtil.Normalize(await node.InnerTextAsync());
            if (text.Contains("次の月") && Regex.IsMatch(text, @"\d{2,4}年\s*\d{1,2}月")) return node;
        }
        throw new WakumyError("カレンダーが見つかりませんでした");
    }

    private async Task<bool> WaitForHeaderAsync(DateOnly d, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (await HeaderDateAsync() == d) return true; // 通信の静まりは待たない。行が揃ったかは ReadDayViewAsync で確認する
            if (DateTime.UtcNow > deadline) return false;
            await _page.WaitForTimeoutAsync(200);
        }
    }

    private async Task<DateOnly?> HeaderDateAsync()
    {
        var heads = _page.Locator("text=/\\d{4}年\\d{1,2}月\\d{1,2}日/");
        if (await heads.CountAsync() == 0) return null;
        return TextUtil.ParseHeaderDate(await heads.First.InnerTextAsync());
    }

    private static async Task<(int, int)?> CalendarMonthAsync(ILocator cal)
    {
        var m = Regex.Match(TextUtil.Normalize(await cal.InnerTextAsync()), @"(\d{2,4})年\s*(\d{1,2})月");
        if (!m.Success) return null;
        var y = int.Parse(m.Groups[1].Value);
        if (y < 100) y += 2000;
        return (y, int.Parse(m.Groups[2].Value));
    }

    /// <summary>表示中の日表示を読み、予約行のリストにする。</summary>
    public async Task<List<ScheduleRow>> ReadDayViewAsync(DateOnly d, string tab)
    {
        await _page.WaitForTimeoutAsync(200); // 日付切替直後は予約取得の通信がまだ始まっていないことがある
        // 通信が 0.5 秒静まるのを待つが、Wakumy が裏で通信し続けていても最大 1.5 秒で先に進む
        // （Playwright 既定の 30 秒待ちだと 1 日あたり 30 秒かかってしまう）
        try { await _page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 1500 }); } catch (TimeoutException) { }
        var area = _page.GetByText("時間帯枠追加").First.Locator("xpath=ancestor::*[contains(., '予約メニュー')][1]");
        var useArea = await area.CountAsync() > 0;
        // 画面は時間枠の骨組みが先に出て、予約の行は後から届く。
        // 枠見出し「1 / 2 枠」の予約数の合計に読めた行数が届き、かつ文字が変わらなくなるまで待つ（最大 8 秒）
        string? prev = null, text = "";
        var rows = new List<ScheduleRow>();
        var expected = 0;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            text = useArea ? await area.InnerTextAsync() : await _page.Locator("body").InnerTextAsync();
            rows = Schedule.ParseDayView(text, d);
            expected = Schedule.ExpectedRows(text);
            if (text == prev && rows.Count >= expected) break;
            if (DateTime.UtcNow > deadline) break;
            prev = text;
            await _page.WaitForTimeoutAsync(300);
        }
        if (rows.Count < expected)
        {
            Log.Warn($"{tab} {d}: 枠の予約数は {expected} 件ですが {rows.Count} 行しか読めませんでした（読み取り不足の可能性）");
            DebugText($"day_{tab}_{d:yyyy-MM-dd}", text);
        }
        else if (DebugDir is not null && rows.Count == 0 && Schedule.HasSlots(text) && !text.Contains("まだ予約はありません"))
            DebugText($"day_{tab}_{d:yyyy-MM-dd}", text); // 枠はあるのに 1 行も読めない → 構造が違う
        return rows;
    }

    private void DebugText(string name, string text)
    {
        if (DebugDir is null) return;
        try
        {
            Directory.CreateDirectory(DebugDir);
            var path = Path.Combine(DebugDir, $"debug_{name}.txt");
            File.WriteAllText(path, text);
            Log.Info($"画面の文字を保存しました: {path}（患者情報を含むので取り扱い注意）");
        }
        catch { }
    }

    private async Task DebugShotAsync(string name)
    {
        if (DebugDir is null) return;
        try
        {
            Directory.CreateDirectory(DebugDir);
            var path = Path.Combine(DebugDir, $"debug_{name}.png");
            await _page.ScreenshotAsync(new() { Path = path, FullPage = true });
            Log.Info($"画面を保存しました: {path}（患者情報が映るので取り扱い注意）");
            // 画面の文字とブラウザ側のエラー（患者情報は含まないはずだが、念のため同じ扱い）
            string[] events;
            lock (_events) events = _events.ToArray();
            var info = new List<string>
            {
                $"url: {_page.Url}",
                $"headless: {_cfg.Headless}",
                $"userAgent: {await _page.EvaluateAsync<string>("navigator.userAgent")}",
                "",
                "--- ブラウザ側のエラー・失敗した通信 ---",
            };
            info.AddRange(events);
            info.Add("");
            info.Add("--- 画面の文字 ---");
            info.Add(await _page.Locator("body").InnerTextAsync());
            File.WriteAllText(Path.Combine(DebugDir, $"debug_{name}.txt"), string.Join("\n", info));
        }
        catch { }
    }
}
