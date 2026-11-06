using System.Globalization;
using System.Text;
using Kenshin.Config;
using Kenshin.Domain;
using Kenshin.Judge;
using Kenshin.Web;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kenshin.Reports;

/// <summary>入力シートの 1 マス（mm、紙の左上が原点）。</summary>
public sealed record SheetCell(float X, float Y, float W, float H);

/// <summary>入力シートの 1 項目。digits＝数字のマス（1 マス 1 文字）、check＝塗りつぶす □。</summary>
public sealed record SheetField(string Key, string Kind, string Option, List<SheetCell> Cells, int Decimals, string Label);

/// <summary>
/// 機械で読める入力シート（A4 縦）の設計図。印刷と読み取りの両方がこの 1 つの座標表を使う。
/// 四隅の黒い四角（位置合わせ）、QR（健診回の番号）、数字のマス、塗りつぶす □ だけで構成し、判定は書かせない（プログラムが付ける）。
/// </summary>
public sealed class SheetTemplate
{
    public const float PageW = 210, PageH = 297;
    public const float MarkSize = 7;                 // 四隅の黒い四角（mm）
    public static readonly (float x, float y)[] Marks = { (8, 8), (PageW - 8 - MarkSize, 8), (8, PageH - 8 - MarkSize), (PageW - 8 - MarkSize, PageH - 8 - MarkSize) };
    public const float QrX = 168, QrY = 12, QrSize = 26;
    public const float CellW = 8.5f, CellH = 11f, Box = 6f;
    public List<SheetField> Fields { get; } = new();
    public List<(float x, float y, string text, float size, bool bold)> Labels { get; } = new();
    public List<(float x, float y, string text)> Marks2 { get; } = new(); // 小数点・スラッシュなどの印字

    float _y;
    const float X0 = 18;   // 左端
    const float LineH = 15f;

    /// <summary>受診者を決めるためのマス（個人ID・実施日・生年月日・性別）。exam の値ではないので「_」で始める。</summary>
    public const string KeyFalcoId = "_falco_id", KeyDate = "_date", KeyBirth = "_birth", KeySex = "_sex";
    public static bool IsHeaderKey(string key) => key.StartsWith("_");

    /// <summary>
    /// 設計図。コースに関係なく同じ並び（看護師が書く検査は全部載せ、無い検査は空欄のまま）。
    /// 白紙（受診者を決めずに印刷）でも、受診者ごとの印刷でも同じ座標なので、読み取りは 1 つの設計図で済む。
    /// </summary>
    public static SheetTemplate Build(ExamContext? x, Masters m)
    {
        var t = new SheetTemplate();
        bool Has(string s) => true;
        // 受診者を決めるマス：個人ID（ファルコの番号）・実施日、生年月日（西暦）・性別。受診者ごとの印刷では印字済み、白紙では看護師が書く
        t._y = 48;
        t.Section("受診者");
        var cx = t.Digits(KeyFalcoId, "個人ID", 6, 0, X0 + 14);
        cx = t.Digits(KeyDate, "実施日", 8, 0, cx + 6); t.Label(cx + 1, "(西暦8桁)", 6.5f);
        t.NextLine();
        cx = t.Digits(KeyBirth, "生年月日", 8, 0, X0 + 14); t.Label(cx + 1, "(西暦8桁)", 6.5f);
        cx = t.Label(cx + 16, "性別", 8.5f); cx = t.Check(KeySex, "男", "男", cx + 1); cx = t.Check(KeySex, "女", "女", cx + 2);
        t.Label(cx + 6, "氏名 ＿＿＿＿＿＿＿＿", 8.5f);
        t.NextLine();
        t.Section("計測");
        cx = t.Digits(Keys.Height, "身長", 3, 1, X0 + 14); cx = t.Unit(cx, "cm");
        cx = t.Digits(Keys.Weight, "体重", 3, 1, cx + 8); cx = t.Unit(cx, "kg");
        cx = t.Digits(Keys.Waist, "腹囲", 3, 1, cx + 8); cx = t.Unit(cx, "cm");
        t.NextLine();
        if (Has(Sections.Vision))
        {
            t.Section("視力");
            cx = t.Digits(Keys.VisionR, "裸眼 右", 1, 1, X0 + 20); cx = t.Digits(Keys.VisionL, "左", 1, 1, cx + 10);
            cx = t.Digits(Keys.VisionRc, "矯正 右", 1, 1, cx + 16); cx = t.Digits(Keys.VisionLc, "左", 1, 1, cx + 10);
            t.NextLine();
        }
        t.Section("血圧");
        cx = t.Digits(Keys.Bp1Sys, "1回目", 3, 0, X0 + 16); cx = t.Slash(cx); cx = t.Digits(Keys.Bp1Dia, "", 3, 0, cx);
        cx = t.Digits(Keys.Bp2Sys, "2回目", 3, 0, cx + 16); cx = t.Slash(cx); cx = t.Digits(Keys.Bp2Dia, "", 3, 0, cx);
        t.NextLine();
        cx = t.Check(Keys.BpMed, "1", "降圧剤を服用している", X0 + 16);
        cx = t.Digits(Keys.MealMinutes, "食後時間", 3, 0, cx + 20); cx = t.Unit(cx, "分");
        t.NextLine();
        if (Has(Sections.Hearing))
        {
            t.Section("聴力");
            foreach (var pair in new[] { new[] { (Keys.HearR1000, "右 1000Hz"), (Keys.HearR4000, "右 4000Hz") }, new[] { (Keys.HearL1000, "左 1000Hz"), (Keys.HearL4000, "左 4000Hz") } })
            {
                cx = X0 + 12;
                foreach (var (key, label) in pair)
                {
                    t.Label(cx, label, 8.5f); cx += 24;
                    cx = t.Check(key, "なし", "なし", cx); cx = t.Check(key, "あり", "あり", cx + 3); cx += 16;
                }
                t.NextLine();
            }
            cx = t.Label(X0 + 12, "会話法", 8.5f) + 16; cx = t.Check(Keys.HearConv, "正常", "正常", cx); cx = t.Check(Keys.HearConv, "異常", "異常", cx + 2);
            t.NextLine();
        }
        if (Has(Sections.Urine))
        {
            t.Section("尿");
            cx = X0 + 12;
            foreach (var (key, label) in new[] { (Keys.UrineSugar, "糖"), (Keys.UrineProtein, "蛋白") })
            {
                t.Label(cx, label, 7.5f); cx += 9;
                foreach (var o in new[] { "−", "±", "+", "2+", "3+" }) cx = t.Check(key, o, o, cx + 0.5f);
                cx += 8;
            }
            t.NextLine();
            cx = X0 + 12; t.Label(cx, "潜血", 7.5f); cx += 9;
            foreach (var o in new[] { "−", "±", "+", "2+", "3+" }) cx = t.Check(Keys.UrineBlood, o, o, cx + 0.5f);
            cx += 8; t.Label(cx, "ｳﾛﾋﾞﾘﾉｰｹﾞﾝ", 7.5f); cx += 17;
            foreach (var o in new[] { "正常", "+", "−" }) cx = t.Check(Keys.UrineUrobil, o, o, cx + 0.5f);
            cx = t.Check(Keys.UrineMenstruating, "1", "生理中", cx + 8);
            t.NextLine();
        }
        if (Has(Sections.Stool))
        {
            t.Section("便");
            cx = X0 + 12; t.Label(cx, "1回目", 7.5f); cx += 11; cx = t.Check(Keys.Stool1, "−", "−", cx); cx = t.Check(Keys.Stool1, "+", "+", cx + 1);
            cx += 10; t.Label(cx, "2回目", 7.5f); cx += 11; cx = t.Check(Keys.Stool2, "−", "−", cx); cx = t.Check(Keys.Stool2, "+", "+", cx + 1);
            t.NextLine();
        }
        // 問診（受診者が裏面に書く）と所見（先生が PC で入れる）はこのシートに載せない：看護師が書く部分だけ（2026-11-06 先生）
        t.Labels.Add((X0 - 6, t._y - 2, "問診は受診票の裏面のとおり受診者が記入。所見は先生が画面で入れる。このシートは看護師の計測分だけ。", 7.5f, false));
        return t;
    }

    static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    void Section(string title, bool ownLine = false) { Labels.Add((X0 - 6, _y - 4.5f, title, 8.5f, true)); if (ownLine) _y += 6.5f; }
    void NextLine() { _y += LineH; }
    float Label(float x, string text, float size, bool bold = false) { Labels.Add((x, _y - 3.5f, text, size, bold)); return x + text.Length * size * 0.35f + 2; }
    float Unit(float x, string unit) { Labels.Add((x + 1, _y - 3f, unit, 7.5f, false)); return x + 2 + unit.Length * 3; }
    float Slash(float x) { Marks2.Add((x + 1.5f, _y + 2.5f, "/")); return x + 6; }

    /// <summary>数字のマス。整数 intDigits 桁、小数 decimals 桁（小数点は印字する）。戻りは右端の x。</summary>
    float Digits(string key, string label, int intDigits, int decimals, float x)
    {
        if (label != "") { Labels.Add((x, _y - 3.5f, label, 8.5f, false)); x += label.Length * 3.1f + 2; }
        var cells = new List<SheetCell>();
        for (var i = 0; i < intDigits; i++) { cells.Add(new SheetCell(x, _y - 7f, CellW, CellH)); x += CellW; }
        if (decimals > 0)
        {
            Marks2.Add((x + 0.6f, _y + 3.5f, "．")); x += 3.5f;
            for (var i = 0; i < decimals; i++) { cells.Add(new SheetCell(x, _y - 7f, CellW, CellH)); x += CellW; }
        }
        Fields.Add(new SheetField(key, "digits", "", cells, decimals, label));
        return x;
    }

    /// <summary>塗りつぶす □ と、その右の文字。戻りは右端の x。</summary>
    float Check(string key, string option, string label, float x)
    {
        Fields.Add(new SheetField(key, "check", option, new List<SheetCell> { new(x, _y - 4.3f, Box, Box) }, 0, label));
        Labels.Add((x + Box + 1.0f, _y - 3.6f, label, 8.5f, false));
        return x + Box + 2f + label.Length * 3.0f;
    }

    /// <summary>マス・□・四隅の印・区切りの SVG（文字は別に重ねる）。単位 mm。</summary>
    public string Svg(Dictionary<string, string>? prefill = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns='http://www.w3.org/2000/svg' width='{PageW}mm' height='{PageH}mm' viewBox='0 0 {PageW} {PageH}'>");
        foreach (var (mx, my) in Marks) sb.Append(CultureInfo.InvariantCulture, $"<rect x='{mx}' y='{my}' width='{MarkSize}' height='{MarkSize}' fill='#000'/>");
        sb.Append(CultureInfo.InvariantCulture, $"<rect x='{QrX - 1}' y='{QrY - 1}' width='{QrSize + 2}' height='{QrSize + 2}' fill='none' stroke='#999' stroke-width='0.2'/>");
        foreach (var f in Fields)
            foreach (var c in f.Cells)
            {
                if (f.Kind == "digits") sb.Append(CultureInfo.InvariantCulture, $"<rect x='{c.X}' y='{c.Y}' width='{c.W}' height='{c.H}' fill='#fff' stroke='#444' stroke-width='0.3'/>");
                else
                {
                    var filled = prefill != null && prefill.TryGetValue(f.Key, out var pv) && pv.Split(';').Contains(f.Option);
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x='{c.X}' y='{c.Y}' width='{c.W}' height='{c.H}' fill='{(filled ? "#111" : "#fff")}' stroke='#333' stroke-width='0.35'/>");
                }
            }
        // 見出し行の薄い下線
        foreach (var (lx, ly, text, size, bold) in Labels.Where(l => l.bold && l.x < X0))
            sb.Append(CultureInfo.InvariantCulture, $"<line x1='{X0 - 6}' y1='{ly + 4.5f}' x2='{PageW - 12}' y2='{ly + 4.5f}' stroke='#bbb' stroke-width='0.25'/>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}

/// <summary>入力シート（A4）。看護師が手で書き、写真で読む。受診者ごと（印字済み）でも白紙でも同じ座標。</summary>
public sealed class InputSheetDocument : IDocument
{
    readonly AppState _state;
    readonly Masters _m;
    readonly ExamContext? _x;
    readonly Dictionary<string, string>? _prefill;
    readonly bool _handwriting;
    /// <param name="prefill">記入済みとして印字する値（テスト用）。digits はキー→数値、check はキー→選択肢</param>
    /// <param name="handwriting">true なら記入値を手書き風（少しずれ・傾き）に印字する（テスト撮影用）</param>
    public InputSheetDocument(ReportData d, Dictionary<string, string>? prefill = null, bool handwriting = false) : this(d.State, d.M, d.Now, prefill, handwriting) { }
    public InputSheetDocument(AppState state, Masters m, ExamContext? x, Dictionary<string, string>? prefill = null, bool handwriting = false)
    { _state = state; _m = m; _x = x; _prefill = prefill; _handwriting = handwriting; }
    public DocumentMetadata GetMetadata() => new() { Title = "健診 入力シート" };
    static float Pt(float mm) => mm * 72f / 25.4f;
    static string Ymd8(string ymd) => ymd.Length == 10 ? ymd.Replace("-", "") : "";
    static string DigitsOnly(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>受診者ごとの印刷で、受診者を決めるマスに印字する値（個人ID・実施日・生年月日・性別）。</summary>
    public static Dictionary<string, string> HeaderPrefill(ExamContext x)
    {
        var p = x.Patient; var e = x.Exam;
        var id = DigitsOnly(p.FalcoId != "" ? p.FalcoId : p.ChartNo);
        var d = new Dictionary<string, string>();
        if (id != "" && id.Length <= 6) d[SheetTemplate.KeyFalcoId] = id;
        if (Ymd8(e.ExamDate) != "") d[SheetTemplate.KeyDate] = Ymd8(e.ExamDate);
        if (Ymd8(p.BirthDate) != "") d[SheetTemplate.KeyBirth] = Ymd8(p.BirthDate);
        if (p.Sex is "男" or "女") d[SheetTemplate.KeySex] = p.Sex;
        return d;
    }

    public void Compose(IDocumentContainer container)
    {
        var t = SheetTemplate.Build(_x, _m);
        var fill = new Dictionary<string, string>(_prefill ?? new());
        if (_x != null) foreach (var (k, v) in HeaderPrefill(_x)) fill.TryAdd(k, v);
        var rnd = new Random(_x?.Exam.Id is { } eid ? (int)(eid * 7919) : 1234);
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(0);
            page.DefaultTextStyle(s => s.FontFamily(ReportService.Font).FontSize(8));
            page.Content().Layers(layers =>
            {
                layers.PrimaryLayer().Svg(t.Svg(fill));
                void Text(float mx, float my, string s, float size, bool bold = false, float width = 120, float rotate = 0)
                {
                    var c = layers.Layer().Unconstrained().TranslateX(Pt(mx)).TranslateY(Pt(my));
                    if (rotate != 0) c = c.Rotate(rotate);
                    var span = c.Width(Pt(width)).Text(s).FontSize(size);
                    if (bold) span.Bold();
                }
                Text(18, 9, _x != null ? "健診 入力シート" : "健診 入力シート（白紙）", 15, true);
                Text(70, 11, _state.Config.Clinic.Name, 8);
                if (_x != null)
                {
                    var p = _x.Patient; var e = _x.Exam;
                    Text(18, 18, $"氏名 {p.Name}（{p.Kana}）  {p.Sex}  {ReportService.Wareki(p.BirthDate)}", 9);
                    Text(18, 24, $"個人ID {(p.FalcoId != "" ? p.FalcoId : p.ChartNo)}   事業所 {p.Office}   コース {e.Course}   実施日 {e.ExamDate.Replace('-', '/')}   健診回 {e.Id}", 8);
                    layers.Layer().Unconstrained().TranslateX(Pt(SheetTemplate.QrX)).TranslateY(Pt(SheetTemplate.QrY)).Width(Pt(SheetTemplate.QrSize)).Image(ReportService.Qr($"kenshin:exam:{e.Id}"));
                }
                else
                {
                    Text(18, 18, "受診者を登録せずに使う白紙。個人ID（ファルコの番号）・実施日・生年月日・性別を下のマスに書く。", 8, false, 150);
                    Text(18, 24, "写真から同じ受診者にまとまる。個人ID が無ければ生年月日と性別で探す。氏名は人が読む用（読み取らない）。", 7.5f, false, 150);
                }
                Text(18, 31, "書き方：数字は 1 マスに 1 文字、はっきり大きく。□ は ✓ ではなく黒く塗りつぶす。判定は書かなくてよい（プログラムが付ける）。", 7, false, 150);
                Text(18, 35.5f, "読み取りのため：四隅の黒い四角と QR を隠さない。用紙全体が入るよう真上から撮る。無い検査の欄は空のままでよい。", 7, false, 150);
                foreach (var (lx, ly, s, size, bold) in t.Labels) Text(lx, ly, s, size, bold, bold ? 170 : 80);
                foreach (var (mx, my, s) in t.Marks2) Text(mx, my - 4, s, 10, true, 10);
                foreach (var f in t.Fields.Where(f => f.Kind == "digits" && fill.ContainsKey(f.Key)))
                {
                    // 右詰めで 1 マス 1 文字（小数は小数点の右のマスへ）。印字済みの受診者マスは機械的に、手書き風はずらして
                    var hand = _handwriting && !(SheetTemplate.IsHeaderKey(f.Key) && _x != null);
                    var v = fill[f.Key]; var parts = v.Split('.'); var ip = parts[0]; var dp = parts.Length > 1 ? parts[1] : "";
                    var intN = f.Cells.Count - f.Decimals;
                    void Digit(SheetCell c, char ch)
                    {
                        if (!hand) { Text(c.X + 2.2f, c.Y + 1.2f, ch.ToString(), 19, true, 10); return; }
                        var dx = (float)(rnd.NextDouble() * 2.0 - 1.0); var dy = (float)(rnd.NextDouble() * 1.6 - 0.8); var rot = (float)(rnd.NextDouble() * 16 - 8); var sz = 17 + (float)rnd.NextDouble() * 4;
                        Text(c.X + 2.0f + dx, c.Y + 1.0f + dy, ch.ToString(), sz, false, 10, rot);
                    }
                    for (var i = 0; i < intN; i++) { var k = i - (intN - ip.Length); if (k >= 0 && k < ip.Length) Digit(f.Cells[i], ip[k]); }
                    for (var i = 0; i < f.Decimals; i++) if (i < dp.Length) Digit(f.Cells[intN + i], dp[i]);
                }
                if (_x != null) Text(18, SheetTemplate.PageH - 14, $"健診回 {_x.Exam.Id}  {_x.Patient.Name}  {_x.Exam.ExamDate}  — 入力シート", 7);
                else Text(18, SheetTemplate.PageH - 14, "入力シート（白紙）— 個人ID・実施日・生年月日・性別のマスで受診者を決める", 7);
            });
        });
    }
}
