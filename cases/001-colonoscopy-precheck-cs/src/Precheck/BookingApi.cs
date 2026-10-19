using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Precheck;

/// <summary>
/// 予約一覧の画面が裏で受け取っている予約データ（POST /core/searchItems/bookings）を扱う。
/// 画面が週表示を開くときに送る問い合わせを 1 つ捕まえ、日付の範囲だけを書き換えて送り直すと、
/// 日送りなしで期間全体の予約を 1 回で受け取れる。ここは Playwright に依存しない部分（テスト可）。
/// </summary>
public static class BookingApi
{
    public const string PathSuffix = "/core/searchItems/bookings";
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    /// <summary>
    /// 送信データ（JSON）の中の日付範囲（_between の 2 つの日付、または _gte/_lte の組）を from〜to に書き換える。
    /// 範囲が見つからなければ null（この画面の問い合わせの形が想定と違う）。
    /// </summary>
    public static string? RewriteDateRange(string postData, DateOnly from, DateOnly to)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(postData); } catch { return null; }
        if (root is null) return null;
        var n = Rewrite(root, from, to);
        return n > 0 ? root.ToJsonString() : null;
    }

    private static int Rewrite(JsonNode node, DateOnly from, DateOnly to)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                {
                    if (kv.Value is null) continue;
                    if (kv.Key == "_between" && kv.Value is JsonArray arr && arr.Count == 2
                        && arr[0]?.GetValue<string>() is string a && arr[1]?.GetValue<string>() is string b
                        && IsoDate.IsMatch(a) && IsoDate.IsMatch(b))
                    {
                        obj["_between"] = new JsonArray(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
                        count++;
                        continue;
                    }
                    if ((kv.Key == "_gte" || kv.Key == "_gt") && kv.Value is JsonValue v1 && v1.TryGetValue<string>(out var s1) && IsoDate.IsMatch(s1))
                    {
                        obj[kv.Key] = from.ToString("yyyy-MM-dd"); count++; continue;
                    }
                    if ((kv.Key == "_lte" || kv.Key == "_lt") && kv.Value is JsonValue v2 && v2.TryGetValue<string>(out var s2) && IsoDate.IsMatch(s2))
                    {
                        obj[kv.Key] = to.ToString("yyyy-MM-dd"); count++; continue;
                    }
                    count += Rewrite(kv.Value, from, to);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    if (item is not null) count += Rewrite(item, from, to);
                break;
        }
        return count;
    }

    /// <summary>受信データ（{"data":[...]}）を予約行に変換する。</summary>
    public static List<ScheduleRow> ParseBookings(string json)
    {
        var rows = new List<ScheduleRow>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("受信データに data がありません");
        foreach (var b in data.EnumerateArray())
        {
            var dateText = Str(b, "bookingDate");
            if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", out var day)) continue;
            var timeText = Str(b, "dailyBookingTimeFrameStartTime");
            var start = day.ToDateTime(TimeOnly.TryParse(timeText, out var t) ? t : TimeOnly.MinValue);
            var status = Str(b, "status");
            var sei = Str(b, "nameSei");
            var mei = Str(b, "nameMei");
            var name = TextUtil.NormalizeName($"{sei} {mei}");
            var kana = TextUtil.NormalizeName($"{Str(b, "nameSeiKana")} {Str(b, "nameMeiKana")}");
            var cardNo = "";
            if (b.TryGetProperty("patientId", out var p) && p.ValueKind == JsonValueKind.Object)
                cardNo = TextUtil.Normalize(Str(p, "registerId"));
            var menu = "";
            if (b.TryGetProperty("bookingMenuId", out var m) && m.ValueKind == JsonValueKind.Object)
                menu = TextUtil.Normalize(Str(m, "name"));
            var birth = DateOnly.TryParseExact(Str(b, "birthDate"), "yyyy-MM-dd", out var bd) ? TextUtil.ToWareki(bd) : "";
            var no = Str(b, "assignedNumber");
            if (name.Length == 0 && cardNo.Length == 0) continue; // 患者のいない枠（空き枠など）
            rows.Add(new ScheduleRow(day, start, status, menu, name, kana, cardNo, birth, no));
        }
        return rows;
    }

    private static string Str(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }
}
