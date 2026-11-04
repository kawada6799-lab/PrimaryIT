using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kenshin.Domain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Kenshin.Lab;

public sealed class ParsedLabReport
{
    public string FileName { get; set; } = "";
    public string TakenAt { get; set; } = "";
    public string ReceivedAt { get; set; } = "";
    public string ReportedAt { get; set; } = "";
    public string RequestNo { get; set; } = "";
    public string PatientHint { get; set; } = "";
    public string IdHint { get; set; } = "";
    public string NameHint { get; set; } = "";
    public List<BloodResult> Items { get; } = new();
    public List<string> Lines { get; } = new();
    public string Error { get; set; } = "";
}

/// <summary>
/// 文字 PDF（HR-f の検査結果画面を Microsoft Print to PDF で印刷したもの、またはファルコの報告書）から
/// 「項目名・結果・単位・基準値・フラグ」の行を抜き出す。OCR はしない。画像 PDF は読めない（Lines が空になる）。
/// 行の読み方はこのクラスだけに集める。実物のレイアウトが分かったらここを直す。
/// </summary>
public static class FalcoPdfReader
{
    static readonly Regex NumberRe = new(@"^[<>＜＞≦≧]?=?\s*-?\d+(?:\.\d+)?$");
    static readonly Regex UnitRe = new(@"^(U/L|IU/L|mg/dL|g/dL|%|fL|pg|×10[²³⁴]?\s*/\s*[μµu]L|x10\^?\d\s*/[μµu]L|万/[μµu]L|百/[μµu]L|/[μµu]L|mEq/L|[μµu]g/dL|ng/mL|U/mL|IU/mL|mIU/mL|pg/mL|mL/分/1\.73㎡?|mL/min/1\.73m?²?|mmol/L|mg/L|秒|mmHg)$", RegexOptions.IgnoreCase);
    static readonly Regex RangeRe = new(@"^(-?\d+(?:\.\d+)?)\s*[~〜～\-ー－]\s*(-?\d+(?:\.\d+)?)$");
    static readonly Regex OneSidedRe = new(@"^(-?\d+(?:\.\d+)?)\s*(以下|以上|未満|超)$");
    static readonly Regex DateRe = new(@"[’'‘]?(\d{2,4})[/\.年](\d{1,2})[/\.月](\d{1,2})");
    static readonly string[] Qualitative = { "陰性", "陽性", "(-)", "(+)", "（-）", "（+）", "(−)", "（−）", "(±)", "（±）", "正常", "異常", "-", "−", "+", "±", "2+", "3+" };
    static readonly string[] FlagTokens = { "↑", "↓", "H", "L", "*H", "*L", "▲", "▼" };

    public static ParsedLabReport ReadFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var rep = ext is ".csv" or ".txt" or ".tsv" ? ReadText(File.ReadAllText(path, DetectEncoding(path))) : ReadPdf(File.ReadAllBytes(path));
        rep.FileName = Path.GetFileName(path);
        return rep;
    }

    static Encoding DetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;
        try { new UTF8Encoding(false, true).GetString(bytes); return Encoding.UTF8; }
        catch { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(932); }
    }

    public static ParsedLabReport ReadPdf(byte[] bytes)
    {
        var rep = new ParsedLabReport();
        try
        {
            using var doc = PdfDocument.Open(bytes);
            foreach (var page in doc.GetPages())
                foreach (var line in LinesOf(page)) rep.Lines.Add(line);
        }
        catch (Exception ex) { rep.Error = "PDF を読めませんでした: " + ex.Message; return rep; }
        if (rep.Lines.Count == 0) rep.Error = "文字が入っていない PDF です（画像だけの PDF は読めません）";
        ParseLines(rep);
        return rep;
    }

    public static ParsedLabReport ReadText(string text)
    {
        var rep = new ParsedLabReport();
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.TrimEnd('\r').Trim();
            if (l.Length > 0) rep.Lines.Add(l.Replace('\t', ' ').Replace(',', ' '));
        }
        ParseLines(rep);
        return rep;
    }

    /// <summary>単語を Y 座標で行にまとめ、X 順に並べる。</summary>
    static IEnumerable<string> LinesOf(Page page)
    {
        var words = page.GetWords().ToList();
        var rows = new List<(double y, List<Word> ws)>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var y = w.BoundingBox.Bottom;
            var row = rows.FirstOrDefault(r => Math.Abs(r.y - y) < Math.Max(2.0, w.BoundingBox.Height * 0.5));
            if (row.ws == null) rows.Add((y, new List<Word> { w })); else row.ws.Add(w);
        }
        foreach (var r in rows.OrderByDescending(r => r.y))
        {
            var ws = r.ws.OrderBy(w => w.BoundingBox.Left).ToList();
            var sb = new StringBuilder();
            Word? prev = null;
            foreach (var w in ws)
            {
                if (prev != null)
                {
                    var gap = w.BoundingBox.Left - prev.BoundingBox.Right;
                    sb.Append(gap > prev.BoundingBox.Height * 0.3 ? "  " : " ");
                }
                sb.Append(w.Text); prev = w;
            }
            yield return sb.ToString().Trim();
        }
    }

    static void ParseLines(ParsedLabReport rep)
    {
        var sort = 0;
        var head = new List<string>();
        foreach (var line in rep.Lines)
        {
            if (line.Contains("採取日") || line.Contains("受付日") || line.Contains("報告日"))
            {
                foreach (Match dm in DateRe.Matches(line))
                {
                    var d = ToDate(dm);
                    var idx = dm.Index;
                    var before = line.Substring(0, idx);
                    var label = before.LastIndexOf("採取日", StringComparison.Ordinal) >= 0 && before.LastIndexOf("採取日", StringComparison.Ordinal) > Math.Max(before.LastIndexOf("受付日", StringComparison.Ordinal), before.LastIndexOf("報告日", StringComparison.Ordinal)) ? "採取"
                        : before.LastIndexOf("報告日", StringComparison.Ordinal) > before.LastIndexOf("受付日", StringComparison.Ordinal) ? "報告" : "受付";
                    if (label == "採取" && rep.TakenAt == "") rep.TakenAt = d;
                    else if (label == "報告" && rep.ReportedAt == "") rep.ReportedAt = d;
                    else if (label == "受付" && rep.ReceivedAt == "") rep.ReceivedAt = d;
                }
            }
            var req = Regex.Match(line, @"\b(\d{2}-\d{3}-\d{3})\b");
            if (req.Success && rep.RequestNo == "") rep.RequestNo = req.Groups[1].Value;
            if (head.Count < 12 && rep.Items.Count == 0) head.Add(line);
            var item = ParseItemLine(line);
            if (item != null) { item.Sort = sort++; rep.Items.Add(item); }
        }
        rep.PatientHint = string.Join(" / ", head.Where(h => h.Contains("様") || h.Contains("ID") || h.Contains("氏名") || Regex.IsMatch(h, @"\d{4,}")).Take(4));
        var id = Regex.Match(rep.PatientHint, @"(?:ID|個人|番号)[^\d]{0,6}(\d{3,12})");
        if (id.Success) rep.IdHint = id.Groups[1].Value;
        // 「様」の付く行のうち、ID や数字を含む行を優先（最初の「○○クリニック 様」は依頼元）
        foreach (var h in head.Where(h => h.Contains("様")).OrderByDescending(h => h.Contains("ID") || Regex.IsMatch(h, @"\d{3,}") ? 1 : 0))
        {
            var name = Regex.Match(h, @"(?:ID\s*\d+\s*)?([^\s/\d]+(?:\s+[^\s/\d]+)?)\s*様");
            if (name.Success) { rep.NameHint = name.Groups[1].Value.Trim(); break; }
        }
        if (rep.TakenAt == "") rep.TakenAt = rep.ReceivedAt;
    }

    static string ToDate(Match m)
    {
        var y = int.Parse(m.Groups[1].Value); if (y < 100) y += 2000;
        return $"{y:D4}-{int.Parse(m.Groups[2].Value):D2}-{int.Parse(m.Groups[3].Value):D2}";
    }

    /// <summary>1 行を「項目名 [フラグ] 結果 [単位] [基準値]」として読む。読めなければ null。</summary>
    public static BloodResult? ParseItemLine(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => Num.ToHalfWidth(t)).ToList();
        if (tokens.Count < 2) return null;
        // 結果の位置：最初の数値 or 定性語
        var valueIdx = -1;
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (IsValueToken(t)) { valueIdx = i; break; }
        }
        if (valueIdx < 0) return null;
        var nameTokens = tokens.Take(valueIdx).Where(t => !FlagTokens.Contains(t)).ToList();
        if (nameTokens.Count == 0) return null;
        var name = string.Join(" ", nameTokens);
        if (name.Length < 2 || Regex.IsMatch(name, @"^[\d\.\-/:' ]+$")) return null;
        if (name.Contains("受付日") || name.Contains("報告日") || name.Contains("採取日") || name.Contains("基準値") || name.Contains("検査項目") || name.Contains("ID") || name.Contains("氏名") || line.Contains("様") || name.Contains("生年月日")) return null;
        var flag = tokens.Take(valueIdx).FirstOrDefault(t => FlagTokens.Contains(t)) ?? "";
        var value = tokens[valueIdx];
        var i2 = valueIdx + 1;
        // 「0.1 以下」「0.05 未満」のように結果と修飾が分かれている場合
        if (i2 < tokens.Count && tokens[i2] is "以下" or "未満" or "以上") { value += tokens[i2]; i2++; }
        if (i2 < tokens.Count && tokens[i2] is "陰性" or "陽性" && !Qualitative.Contains(value)) { value += " " + tokens[i2]; i2++; }
        if (i2 < tokens.Count && FlagTokens.Contains(tokens[i2])) { flag = tokens[i2]; i2++; }
        var unit = "";
        if (i2 < tokens.Count && UnitRe.IsMatch(tokens[i2])) { unit = tokens[i2]; i2++; }
        else if (i2 + 1 < tokens.Count && UnitRe.IsMatch(tokens[i2] + tokens[i2 + 1])) { unit = tokens[i2] + tokens[i2 + 1]; i2 += 2; }
        var refText = string.Join(" ", tokens.Skip(i2).Where(t => !FlagTokens.Contains(t) && t != "*")).Trim();
        if (unit == "" && i2 < tokens.Count && tokens[i2] == "%") { unit = "%"; refText = string.Join(" ", tokens.Skip(i2 + 1)).Trim(); }
        var (lo, hi) = ParseRange(refText);
        if (flag == "H" || flag == "*H" || flag == "▲") flag = "↑"; else if (flag == "L" || flag == "*L" || flag == "▼") flag = "↓";
        var r = new BloodResult { Item = name, Value = value, Unit = unit, RefText = refText, RefLow = lo, RefHigh = hi, Flag = flag };
        if (r.Flag == "" && r.NumericValue is { } v && IsNumeric(value)) { if (hi != null && v > hi) r.Flag = "↑"; else if (lo != null && v < lo) r.Flag = "↓"; }
        return r;
    }

    static bool IsNumeric(string s) => NumberRe.IsMatch(s);
    static bool IsValueToken(string t) => IsNumeric(t) || Qualitative.Contains(t) || Regex.IsMatch(t, @"^-?\d+(\.\d+)?(以下|以上|未満)$");

    /// <summary>基準値の文字 → 下限・上限。「39 ~ 98」「2.5以下」「60.0以上」「1.0未満 陰性」「0.05未満」。</summary>
    public static (double? lo, double? hi) ParseRange(string refText)
    {
        if (string.IsNullOrWhiteSpace(refText)) return (null, null);
        var t = Num.ToHalfWidth(refText).Replace(" ", "").Replace("陰性", "").Replace("陽性", "");
        var m = RangeRe.Match(t);
        if (m.Success) return (D(m.Groups[1].Value), D(m.Groups[2].Value));
        m = OneSidedRe.Match(t);
        if (m.Success)
        {
            var v = D(m.Groups[1].Value);
            return m.Groups[2].Value is "以下" or "未満" ? (null, v) : (v, null);
        }
        return (null, null);
        static double? D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
