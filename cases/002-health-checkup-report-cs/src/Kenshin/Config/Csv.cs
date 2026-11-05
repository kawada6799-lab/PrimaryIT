using System.Text;

namespace Kenshin.Config;

/// <summary>小さな CSV 読み取り（ダブルクォート対応、UTF-8）。マスターファイル用。</summary>
public static class Csv
{
    public static List<Dictionary<string, string>> ReadFile(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Read(text);
    }

    public static List<Dictionary<string, string>> Read(string text)
    {
        var rows = ParseRows(text);
        var result = new List<Dictionary<string, string>>();
        if (rows.Count == 0) return result;
        var header = rows[0].Select(h => h.Trim()).ToList();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue;
            var d = new Dictionary<string, string>();
            for (var i = 0; i < header.Count; i++)
                d[header[i]] = i < row.Count ? row[i].Trim() : "";
            result.Add(d);
        }
        return result;
    }

    static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuote)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuote = false;
                }
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuote = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
