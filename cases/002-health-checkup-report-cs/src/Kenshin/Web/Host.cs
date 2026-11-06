using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kenshin.Config;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Kenshin.Web;

public static class Host
{
    public const string PinCookie = "kenshin_pin";

    public static int Run(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var noGuard = args.Contains("--no-guard");
        var noBrowser = args.Contains("--no-browser");
        for (var i = 0; i < args.Length - 1; i++) if (args[i] == "--base") baseDir = Path.GetFullPath(args[i + 1]);

        var config = AppConfig.Load(baseDir);
        for (var i = 0; i < args.Length - 1; i++) if (args[i] == "--port") config.Listen.LocalPort = int.Parse(args[i + 1]);
        var masters = Masters.Load(config.ConfigDir);
        var criteria = Criteria.Load(config.ConfigDir);
        var guard = new NetworkGuard(config.NetworkGuard && !noGuard);
        var state = new AppState(config, masters, criteria, guard);
        if (!config.Ocr.Enabled) state.OcrStatus = "設定で無効";
        else
        {
            try { state.Ocr = Ocr.OcrEngine.TryLoad(config.Resolve(config.Ocr.ModelDir)); state.OcrStatus = state.Ocr?.Status ?? $"モデルがありません（{config.Ocr.ModelDir}）"; }
            catch (Exception ex) { state.OcrStatus = "読み込み失敗: " + ex.Message; }
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = baseDir, WebRootPath = Path.Combine(baseDir, "wwwroot"), Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new ExamService(state));

        var lan = config.Listen.LanAddress?.Trim() ?? "";
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
            k.Listen(IPAddress.Loopback, config.Listen.LocalPort);
            if (lan != "" && IPAddress.TryParse(lan, out var lanIp))
            {
                k.Listen(lanIp, config.Listen.LocalPort);
                var cert = Certs.Ensure(state.DataDir, lan);
                k.Listen(lanIp, config.Listen.LanPort, lo => lo.UseHttps(cert));
            }
        });

        var app = builder.Build();
        var urlLocal = $"http://127.0.0.1:{config.Listen.LocalPort}/";

        app.Use(async (ctx, next) => await Auth(ctx, state, next));
        app.UseDefaultFiles();
        app.UseStaticFiles();
        Api.Map(app, state, app.Services.GetRequiredService<ExamService>());
        var wwwroot = Path.Combine(baseDir, "wwwroot");
        app.MapGet("/unlock", () => Results.File(Path.Combine(wwwroot, "unlock.html"), "text/html; charset=utf-8"));
        app.MapGet("/pin", () => Results.File(Path.Combine(wwwroot, "pin.html"), "text/html; charset=utf-8"));

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var dest = state.BackupOnExit();
            if (dest != null) Console.WriteLine("バックアップを書きました: " + dest);
            state.Dispose();
        });

        var guardStatus = guard.Check();
        try { File.AppendAllText(Path.Combine(baseDir, "kenshin.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 起動 v{typeof(Host).Assembly.GetName().Version} guard={(guardStatus.Blocked ? "停止中" : "OK")}{Environment.NewLine}"); } catch { }
        Console.WriteLine("健診結果作成 kenshin " + typeof(Host).Assembly.GetName().Version);
        Console.WriteLine("PC の画面: " + urlLocal);
        if (lan != "") { Console.WriteLine($"タブレット: http://{lan}:{config.Listen.LocalPort}/  または  https://{lan}:{config.Listen.LanPort}/ （マイクを使うときは https。証明書 data\\ca.crt をタブレットに入れる）"); }
        Console.WriteLine("写真の読み取り: " + state.OcrStatus);
        if (guardStatus.Blocked) Console.WriteLine("【停止中】" + guardStatus.Reason);
        Console.WriteLine("終了するにはこの画面で Ctrl+C。");

        if (config.OpenBrowser && !noBrowser && OperatingSystem.IsWindows()) OpenBrowser(urlLocal);
        app.Run();
        return 0;
    }

    static readonly string[] OpenPrefixes = { "/static/", "/favicon", "/api/status", "/ca.crt", "/unlock", "/api/unlock", "/pin", "/api/pin" };

    static async Task Auth(HttpContext ctx, AppState state, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var isApi = path.StartsWith("/api/");
        var ip = ctx.Connection.RemoteIpAddress;
        var loopback = IsLocalPc(ip);

        // ネットワーク接続の検知：つながっている間は API を止める（画面は状態を見せる）
        if (isApi && path != "/api/status")
        {
            var g = state.Guard.Check();
            if (g.Blocked) { ctx.Response.StatusCode = 423; await ctx.Response.WriteAsJsonAsync(new { error = g.Reason, blocked = true }); return; }
        }
        if (OpenPrefixes.Any(p => path.StartsWith(p)))
        {
            if ((path == "/api/unlock" || path == "/unlock") && !loopback) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("PC の画面からだけ使えます"); return; }
            await next(); return;
        }
        if (!state.Unlocked)
        {
            if (isApi) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = loopback ? "起動パスワードが未入力です" : "PC 側で起動パスワードが未入力です", locked = true }); return; }
            if (loopback) { ctx.Response.Redirect("/unlock"); return; }
            ctx.Response.StatusCode = 503; ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<meta charset=utf-8><body style='font-family:sans-serif;padding:2em'><h2>準備中</h2><p>PC 側でまだ起動パスワードが入力されていません。PC で入力してから、このページを再読み込みしてください。</p></body>"); return;
        }
        if (!loopback && !PinOk(ctx, state))
        {
            if (isApi) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "PIN が必要です", pin = true }); return; }
            ctx.Response.Redirect("/pin"); return;
        }
        await next();
    }

    /// <summary>PC 自身からの接続か（127.0.0.1 / ::1 だけ。127.0.0.2 などはタブレットと同じ扱いにして PIN を求める）。</summary>
    public static bool IsLocalPc(IPAddress? ip)
    {
        if (ip == null) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback);
    }

    public static string PinToken(AppState state, string pin)
    {
        using var h = new HMACSHA256(state.CookieKey);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(pin)));
    }
    static bool PinOk(HttpContext ctx, AppState state)
    {
        if (string.IsNullOrEmpty(state.Config.Pin)) return true;
        var c = ctx.Request.Cookies[PinCookie];
        return c != null && c == PinToken(state, state.Config.Pin);
    }

    static void OpenBrowser(string url)
    {
        try
        {
            foreach (var exe in new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" })
                if (File.Exists(exe)) { Process.Start(new ProcessStartInfo(exe, "--app=" + url) { UseShellExecute = false }); return; }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Console.WriteLine("ブラウザを開けませんでした: " + ex.Message + "  手で " + url + " を開いてください。"); }
    }
}
