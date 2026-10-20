using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Precheck;

/// <summary>
/// 調査用：画面の裏でサーバーから届く JSON を保存する（--dump-api）。
/// 週表示を 1 回で読む版を作るために、どんな項目が届いているかを調べる。
/// - 全文は state\debug_api\NNN_*.json に保存（患者情報を含む。開発者に送らない）
/// - summary.txt には項目名と型だけを書き、氏名などの値は伏せる（これだけを開発者に送る）
/// </summary>
public sealed class ApiDump
{
    private readonly string _dir;
    private readonly ConcurrentQueue<Task> _pending = new();
    private readonly ConcurrentQueue<(int No, string Method, string Url, string? PostData, int Status, string Body)> _items = new();
    private int _count;

    public ApiDump(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public void Attach(IPage page)
    {
        page.Response += (_, r) => _pending.Enqueue(CaptureAsync(r));
    }

    private async Task CaptureAsync(IResponse r)
    {
        try
        {
            var ct = r.Headers.TryGetValue("content-type", out var v) ? v : "";
            var url = r.Url;
            if (!ct.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                // content-type が json でなくても中身が JSON のことがある（.json 以外の静的ファイルは除く）
                if (Regex.IsMatch(url, @"\.(js|css|png|jpg|gif|svg|woff2?|ttf|ico|html?)(\?|$)", RegexOptions.IgnoreCase)) return;
            }
            string body;
            try { body = await r.TextAsync(); } catch { return; }
            var t = body.TrimStart();
            if (!(t.StartsWith('{') || t.StartsWith('['))) return;
            var no = Interlocked.Increment(ref _count);
            _items.Enqueue((no, r.Request.Method, url, r.Request.PostData, r.Status, body));
        }
        catch { }
    }

    /// <summary>届いたものを全部書き出し、summary.txt の場所を返す。</summary>
    public async Task<string> SaveAsync()
    {
        while (_pending.TryDequeue(out var t)) { try { await t; } catch { } }
        var sb = new StringBuilder();
        sb.AppendLine("# 通信の調査結果（項目名と型だけ。氏名などの値は伏せてある）");
        sb.AppendLine($"# {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (var it in _items.OrderBy(i => i.No))
        {
            var name = SafeName(it.Url);
            var full = Path.Combine(_dir, $"{it.No:000}_{name}.json");
            try { File.WriteAllText(full, it.Body, new UTF8Encoding(false)); } catch { }

            sb.AppendLine($"## {it.No:000} {it.Method} {RedactUrl(it.Url)}  (HTTP {it.Status}, {it.Body.Length:N0} 文字)");
            if (!string.IsNullOrEmpty(it.PostData))
            {
                sb.AppendLine("送信データ:");
                sb.AppendLine(Indent(ApiJsonSummary.Describe(it.PostData), "  "));
            }
            sb.AppendLine("受信データ:");
            sb.AppendLine(Indent(ApiJsonSummary.Describe(it.Body), "  "));
            sb.AppendLine();
        }
        var summary = Path.Combine(_dir, "summary.txt");
        File.WriteAllText(summary, sb.ToString(), new UTF8Encoding(true));
        return summary;
    }

    private static string SafeName(string url)
    {
        var path = url;
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        var m = Regex.Match(path, @"https?://[^/]+(/.*)?$");
        if (m.Success) path = m.Groups[1].Value;
        path = Regex.Replace(path, @"[^A-Za-z0-9_\-]+", "_").Trim('_');
        return path.Length == 0 ? "root" : path[..Math.Min(60, path.Length)];
    }

    /// <summary>URL の問い合わせ部分は、日付や ID らしいものはそのまま、長い値は伏せる。</summary>
    private static string RedactUrl(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0) return url;
        var parts = url[(q + 1)..].Split('&').Select(p =>
        {
            var kv = p.Split('=', 2);
            if (kv.Length < 2) return p;
            return $"{kv[0]}={ApiJsonSummary.RedactValue(kv[0], Uri.UnescapeDataString(kv[1]))}";
        });
        return url[..q] + "?" + string.Join("&", parts);
    }

    private static string Indent(string s, string indent) =>
        string.Join("\n", s.Split('\n').Select(l => indent + l));
}
