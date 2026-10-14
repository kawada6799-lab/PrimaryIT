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
        var context = await _browser.NewContextAsync(new()
        {
            Locale = "ja-JP",
            ViewportSize = new() { Width = 1400, Height = 900 },
        });
        context.SetDefaultTimeout(_cfg.TimeoutMs);
        _page = await context.NewPageAsync();
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

    private ILocator HeaderNav(string label) =>
        _page.GetByRole(AriaRole.Link, new() { Name = label })
             .Or(_page.GetByRole(AriaRole.Button, new() { Name = label })).First;

    // ---------- 予約一覧（日表示）を日付ごとに読む ----------
    /// <summary>ヘッダーの「予約一覧」→ 診療科タブ（内視鏡 / 外来診察 …）→ 日表示。</summary>
    public async Task OpenScheduleTabAsync(string tab)
    {
        await HeaderNav("予約一覧").ClickAsync();
        await _page.GetByText("時間帯枠追加").First.WaitForAsync();
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
            if (await HeaderDateAsync() == d)
            {
                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                return true;
            }
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
        await _page.WaitForTimeoutAsync(300); // 日付切替直後は予約取得の通信がまだ始まっていないことがある
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
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
            await _page.WaitForTimeoutAsync(400);
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
        }
        catch { }
    }
}
