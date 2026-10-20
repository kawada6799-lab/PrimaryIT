using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Precheck;

/// <summary>JSON の構造を、個人情報らしい値を伏せた形で書き出す（--dump-api の summary.txt 用）。</summary>
public static class ApiJsonSummary
{
    // 値を見せてもよい項目名（判定に使いそうなもの）と、必ず伏せる項目名（個人情報）
    private static readonly Regex ShowKey = new(
        @"status|state|menu|type|kind|category|course|date|time|start|end|from|to|flag|mode|view|tab|department|section|_id$|^id$|code|count|total|page|limit|week|day|month|year|color|web|cancel|visit|reserve|reservation|slot|frame|label|title",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HideKey = new(
        @"name|kana|sei|mei|tel|phone|mail|address|addr|memo|note|comment|birth|zip|postal|card|karte|patient_no|patient_number|chart|insurance|password|token|session",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 「name」を含んでいても、メニュー名・診療科名・ステータス名などは判定に必要なので見せる
    private static readonly Regex AlwaysShowKey = new(
        @"menu|course|department|section|category|status|state|kind|label|title|clinic|hospital|room|tab|view|color",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string RedactValue(string key, string value)
    {
        if (AlwaysShowKey.IsMatch(key) && value.Length <= 60) return value;
        if (HideKey.IsMatch(key)) return $"<伏せ字 {value.Length}文字>";
        if (ShowKey.IsMatch(key) && value.Length <= 40) return value;
        // 項目名で判断できない短い値：日付・時刻・数字・英数字の記号だけなら見せる
        if (value.Length <= 20 && Regex.IsMatch(value, @"^[0-9A-Za-z_\-:/ .T+]*$")) return value;
        return $"<伏せ字 {value.Length}文字>";
    }

    /// <summary>JSON の構造を、値を伏せた形で書き出す。</summary>
    public static string Describe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            Describe(doc.RootElement, sb, "", 0, "(root)");
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return "(JSON として読めない)";
        }
    }

    private static void Describe(JsonElement e, StringBuilder sb, string indent, int depth, string key)
    {
        if (depth > 8) { sb.AppendLine($"{indent}{key}: ..."); return; }
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.AppendLine($"{indent}{key}: {{ }} ({e.EnumerateObject().Count()} 項目)");
                foreach (var p in e.EnumerateObject())
                    Describe(p.Value, sb, indent + "  ", depth + 1, p.Name);
                break;
            case JsonValueKind.Array:
                var n = e.GetArrayLength();
                sb.AppendLine($"{indent}{key}: [ ] ({n} 件)");
                if (n > 0)
                {
                    // 先頭の要素の構造だけ。要素ごとに項目が違う場合に備えて、先頭 3 件の項目名を合わせて出す
                    var first = e[0];
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        var keys = new List<string>();
                        foreach (var el in e.EnumerateArray().Take(3))
                            foreach (var p in el.EnumerateObject())
                                if (!keys.Contains(p.Name)) keys.Add(p.Name);
                        sb.AppendLine($"{indent}  [0]: {{ }} ({keys.Count} 項目)");
                        foreach (var k in keys)
                        {
                            if (first.TryGetProperty(k, out var v)) Describe(v, sb, indent + "    ", depth + 2, k);
                            else sb.AppendLine($"{indent}    {k}: (先頭の要素には無い)");
                        }
                    }
                    else Describe(first, sb, indent + "  ", depth + 1, "[0]");
                }
                break;
            case JsonValueKind.String:
                sb.AppendLine($"{indent}{key}: 文字列 = {RedactValue(key, e.GetString() ?? "")}");
                break;
            case JsonValueKind.Number:
                sb.AppendLine(HideKey.IsMatch(key) && !AlwaysShowKey.IsMatch(key) ? $"{indent}{key}: 数値 = <伏せ字>" : $"{indent}{key}: 数値 = {e.GetRawText()}");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                sb.AppendLine($"{indent}{key}: 真偽 = {e.GetRawText()}");
                break;
            default:
                sb.AppendLine($"{indent}{key}: null");
                break;
        }
    }

}
