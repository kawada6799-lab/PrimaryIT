using System.Text.RegularExpressions;

namespace Precheck;

/// <summary>
/// 予約一覧（日表示）の画面文字列（innerText）を予約行に変換する純粋関数。
/// 画面は table ではなく、時間枠の見出し「13:30 - 14:00 1/2枠 (WEB 0/2)」の下に予約行が並ぶ。
/// 1 行は改行区切りで  予約 / E8 / 1234 / S50.01.01 / (51歳) / テスト タロウ / テスト 太郎 / 連鎖(人間ドック) / メモ…  の順。
/// </summary>
public static class Schedule
{
    private static readonly Regex Slot = new(@"^(\d{1,2}):(\d{2})\s*[-～〜]\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);
    private static readonly Regex Status = new(@"^(予約|仮予約|来院|受付|受付済|診察中|会計|終了|患者都合キャンセル|医院都合キャンセル|キャンセル|無断キャンセル)$", RegexOptions.Compiled);
    private static readonly Regex CardNo = new(@"^\d{1,8}$", RegexOptions.Compiled);
    private static readonly Regex Birth = new(@"^[MTSHR]\d{1,2}\.\d{1,2}\.\d{1,2}$", RegexOptions.Compiled);
    private static readonly Regex Age = new(@"^[（(]\d{1,3}歳[)）]$", RegexOptions.Compiled);
    private static readonly Regex Kana = new(@"^[゠-ヿｦ-ﾟー\s・]+$", RegexOptions.Compiled);
    private static readonly Regex ReservationNo = new(@"^[A-Za-z]{1,3}\d{1,4}$", RegexOptions.Compiled);
    private static readonly Regex Gender = new(@"\s*[♂♀]\s*$", RegexOptions.Compiled);
    private static readonly Regex Noise = new(
        @"^(\+\s*新規予約追加|まだ予約はありません|\d+\s*/\s*\d+\s*枠.*|[（(]WEB.*|問診依頼|メンズ|レディース|♂|♀|WEB|LINE|TEL|電話|窓口|新患|●|○|◯)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>時間枠の見出しが 1 つでもあるか（休診日との区別用）。</summary>
    public static bool HasSlots(string text) =>
        text.Split('\n').Any(l => Slot.IsMatch(TextUtil.Normalize(l)));

    public static List<ScheduleRow> ParseDayView(string text, DateOnly day)
    {
        var lines = text.Split('\n').Select(TextUtil.Normalize).Where(l => l.Length > 0).ToList();
        var rows = new List<ScheduleRow>();
        DateTime? slotStart = null;
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var m = Slot.Match(line);
            if (m.Success)
            {
                slotStart = new DateTime(day.Year, day.Month, day.Day, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
                i++;
                continue;
            }
            if (Status.IsMatch(line) && slotStart is DateTime start)
            {
                // ステータス行から次のステータス行／時間枠行の手前までが 1 件
                var j = i + 1;
                var body = new List<string>();
                while (j < lines.Count && !Status.IsMatch(lines[j]) && !Slot.IsMatch(lines[j]))
                {
                    body.Add(lines[j]);
                    j++;
                }
                var row = RowFromBody(line, body, start, day);
                if (row is not null) rows.Add(row);
                i = j;
                continue;
            }
            i++;
        }
        return rows;
    }

    private static ScheduleRow? RowFromBody(string status, List<string> body, DateTime start, DateOnly day)
    {
        body = body.Where(b => !Noise.IsMatch(b)).ToList();
        string reservationNo = "", cardNo = "", birth = "", kana = "", name = "", menu = "";
        var rest = new List<string>();
        var kanaIdx = -1;
        foreach (var b in body)
        {
            if (birth.Length == 0 && Birth.IsMatch(b)) birth = b;
            else if (Age.IsMatch(b)) continue;
            else if (cardNo.Length == 0 && CardNo.IsMatch(b)) cardNo = b;
            else if (reservationNo.Length == 0 && ReservationNo.IsMatch(b)) reservationNo = b;
            else if (kana.Length == 0 && Kana.IsMatch(b) && b.Length >= 2)
            {
                kana = b;
                kanaIdx = rest.Count; // この直後に漢字氏名、その次に予約メニューが来る
            }
            else rest.Add(b);
        }
        rest = rest.Select(r => Gender.Replace(r, "")).ToList();
        if (kanaIdx >= 0 && kanaIdx < rest.Count)
        {
            name = rest[kanaIdx];
            menu = kanaIdx + 1 < rest.Count ? rest[kanaIdx + 1] : "";
        }
        else if (rest.Count > 0)
        {
            name = rest[0];
            menu = rest.Count > 1 ? rest[1] : "";
        }
        if (name.Length == 0 && kana.Length > 0) name = kana;
        if (name.Length == 0 && menu.Length == 0) return null;
        return new ScheduleRow(day, start, status, menu, TextUtil.NormalizeName(name), kana, cardNo, birth, reservationNo);
    }
}
