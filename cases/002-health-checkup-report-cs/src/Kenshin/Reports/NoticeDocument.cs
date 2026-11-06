using System.Globalization;
using System.Text;
using Kenshin.Config;
using Kenshin.Domain;
using Kenshin.Judge;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kenshin.Reports;

/// <summary>
/// 結果のお知らせ（A4 3 ページ。先生が共有した他院の書式 2026-11-06 に合わせた）。
/// 1 ページ目：受診者情報、判定区分の凡例、項目表（基準値・今回・基準値との比較の印・判定・前回・前々回）、推移グラフ。
/// 2 ページ目：検査ごとの所見（今回・前回・前々回と判定）、問診内容、総合判定の文章と区分、メタボ判定、階層化判定。
/// 3 ページ目：表紙（宛名・健診名・実施日・医師名）。
/// </summary>
public sealed class NoticeDocument : IDocument
{
    readonly ReportData _d;
    public NoticeDocument(ReportData d) { _d = d; }
    public DocumentMetadata GetMetadata() => new() { Title = "結果のお知らせ" };

    static readonly string Head = "#e9f3f9";   // 見出しの薄い水色
    static readonly string Band = "#5bb7d9";   // 群の帯
    static readonly string Now = "#fff8d6";    // 今回の列の薄い黄色
    const float Fs = 7.2f;

    /// <summary>開発時の切り分け用：0＝全ページ、1〜3＝そのページだけ。</summary>
    public static int DebugOnlyPage = 0;
    public static int DebugMaxGroups = 0;
    public void Compose(IDocumentContainer container)
    {
        if (DebugOnlyPage is 0 or 1 or 11 or 12 or 13) Results(container);
        if (DebugOnlyPage is 0 or 2) FindingsAndComments(container);
        if (DebugOnlyPage is 0 or 3) Cover(container);
    }

    void Page(IDocumentContainer container, Action<PageDescriptor> extra)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(8, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontFamily(ReportService.Font).FontSize(Fs));
            page.Footer().AlignRight().Text($"{_d.Now.Patient.FalcoId}  {_d.Now.Patient.Name}　{_d.Now.Exam.ExamDate}").FontSize(6.5f).FontColor(Colors.Grey.Darken1);
            extra(page);
        });
    }

    static IContainer Cell(IContainer c, string? bg = null, bool center = false, bool bold = false) =>
        c.Border(0.4f).BorderColor(Colors.Grey.Medium).Background(bg ?? Colors.White).PaddingVertical(1).PaddingHorizontal(2).AlignMiddle().Element(x => center ? x.AlignCenter() : x);

    // ---------- 1 ページ目：項目表とグラフ ----------
    void Results(IDocumentContainer container) => Page(container, page =>
    {
        var now = _d.Now; var p = now.Patient; var e = now.Exam; var E = _d.E;
        var age = p.AgeAt(DateOnly.TryParse(e.ExamDate, out var dd) ? dd : DateOnly.FromDateTime(DateTime.Today));
        page.Content().Column(col =>
        {
            // 受診者
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(34); cd.RelativeColumn(3); cd.ConstantColumn(30); cd.RelativeColumn(3); cd.ConstantColumn(34); cd.RelativeColumn(2); cd.ConstantColumn(30); cd.RelativeColumn(2); });
                void H(string s) => t.Cell().Element(c => Cell(c, Head)).Text(s);
                void D(string s) => t.Cell().Element(c => Cell(c)).Text(s);
                H("団体"); D(p.Office); H("氏名"); D(p.Name + "　様"); H("受診日"); D(e.ExamDate.Replace('-', '/')); H("コース"); D(e.Course);
                H("所属"); D(p.Dept); H("カナ"); D(p.Kana); H("受付番号"); D(e.Id.ToString()); H("社員番号"); D(p.EmployeeNo);
                H("個人ID"); D(p.FalcoId != "" ? p.FalcoId : p.ChartNo); H("性別"); D(p.Sex); H("生年月日"); D(p.BirthDate.Replace('-', '/') + (age != null ? $"（{age}歳）" : "")); H("健診回"); D(e.Id.ToString());
            });
            // 判定区分の凡例
            col.Item().PaddingTop(3).Row(r =>
            {
                r.AutoItem().Background(Band).Padding(2).Text("判定区分").FontColor(Colors.White).Bold();
                r.RelativeItem().Border(0.4f).BorderColor(Band).Padding(2).Text(string.Join("　", _d.M.Categories.Where(c => c.Section == "").OrderBy(c => c.Rank).Select(c => $"{c.Code} {c.Name}")));
            });
            col.Item().PaddingTop(2).Text("▲ 基準値より高い　● 基準値内　▼ 基準値より低い　／　前回・前々回の H・L は基準値より高い・低い").FontSize(6.5f).FontColor(Colors.Grey.Darken2);
            if (DebugOnlyPage == 12) { col.Item().PaddingTop(3).Element(ItemsTable); return; }
            if (DebugOnlyPage == 13) { col.Item().PaddingTop(3).Width(200).Column(c => Charts(c)); return; }
            if (DebugOnlyPage == 11) return;
            col.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem(62).Element(ItemsTable);
                row.ConstantItem(5);
                row.RelativeItem(38).Column(c => Charts(c));
            });
        });
    });

    sealed record NRow(string Item, string Unit, string Ref, Func<ExamContext?, string> Val, Func<ExamContext?, int> Cmp); // Cmp: 1 高い / -1 低い / 0 基準内 / 9 判定なし
    sealed record NGroup(string Name, string Section, List<NRow> Rows);

    void ItemsTable(IContainer container)
    {
        var now = _d.Now; var E = _d.E; var C = _d.C;
        var groups = BuildGroups(now);
        if (DebugMaxGroups > 0) groups = groups.Take(DebugMaxGroups).ToList();
        container.Table(t =>
        {
            // 群の名前は横書き 1 行にする（RowSpan のセルが行より高いと QuestPDF が止まる）
            t.ColumnsDefinition(cd => { cd.ConstantColumn(38); cd.RelativeColumn(2.2f); cd.RelativeColumn(1.4f); cd.ConstantColumn(16); cd.RelativeColumn(1.2f); cd.ConstantColumn(54); cd.RelativeColumn(1.05f); cd.RelativeColumn(1.05f); });
            foreach (var s in new[] { "", "項目（単位）", "基準値", "", "今回\n" + ReportData.Date(_d.Now), "判定", "前回\n" + ReportData.Date(_d.Prev1), "前々回\n" + ReportData.Date(_d.Prev2) })
                t.Cell().Element(c => Cell(c, Head, true)).Text(s).FontSize(6.5f);
            foreach (var g in groups)
            {
                var first = true;
                foreach (var r in g.Rows)
                {
                    // 群の名前は最初の行だけに（RowSpan は使わない。QuestPDF が止まることがあった）
                    t.Cell().Element(c => c.Border(0.4f).BorderColor(Band).Background(Band).Padding(1).AlignMiddle().AlignCenter()).Text(first ? g.Name : "").FontColor(Colors.White).FontSize(6f);
                    t.Cell().Element(c => Cell(c)).Text(r.Item + (r.Unit != "" ? $"（{r.Unit}）" : "")).FontSize(6.8f);
                    t.Cell().Element(c => Cell(c, null, true)).Text(r.Ref).FontSize(6.5f);
                    var cmp = r.Cmp(now);
                    t.Cell().Element(c => Cell(c, Now, true)).Text(cmp == 1 ? "▲" : cmp == -1 ? "▼" : cmp == 0 ? "●" : "").FontColor(cmp == 1 ? Colors.Red.Darken1 : cmp == -1 ? Colors.Blue.Darken1 : Colors.Green.Darken1).FontSize(7f);
                    t.Cell().Element(c => Cell(c, Now, true)).Text(r.Val(now)).FontSize(7.5f);
                    // 判定は群の最初の行に（RowSpan を途中の列に置くと QuestPDF の自動配置が崩れて止まるので使わない）
                    t.Cell().Element(c => Cell(c, Now, true)).Text(first && g.Section != "" ? _d.Judg(now, g.Section) : "").FontSize(6.8f).Bold();
                    first = false;
                    foreach (var x in new[] { _d.Prev1, _d.Prev2 })
                    {
                        var v = x == null ? "" : r.Val(x); var pc = x == null ? 9 : r.Cmp(x);
                        t.Cell().Element(c => Cell(c, null, true)).Text((pc == 1 ? "H " : pc == -1 ? "L " : "") + v).FontSize(6.8f);
                    }
                }
            }
        });
    }

    static string Vertical(string s) => string.Join("\n", s.Select(ch => ch.ToString()));

    List<NGroup> BuildGroups(ExamContext now)
    {
        var E = _d.E; var C = _d.C; var M = _d.M;
        var sex = now.Patient.Sex;
        int CmpRange(double? v, double? lo, double? hi) => v == null ? 9 : hi != null && v > hi ? 1 : lo != null && v < lo ? -1 : 0;
        string Val(string key) => "";
        NRow Num(string item, string unit, string key, string refText, double? lo, double? hi) =>
            new(item, unit, refText, x => x?.V(key) ?? "", x => CmpRange(x?.N(key), lo, hi));
        // 身体計測
        var bmiTier = C.Bmi.Tiers.FirstOrDefault(t => t.Code == "150");
        var waistLimit = now.IsMale ? C.Waist.Male : C.Waist.Female;
        var g = new List<NGroup>
        {
            new("身体計測", Sections.Measure, new()
            {
                new("身長", "cm", "", x => x?.V(Keys.Height) ?? "", _ => 9),
                new("体重", "kg", "", x => x?.V(Keys.Weight) ?? "", _ => 9),
                new("標準体重", "kg", "", x => x == null ? "" : Domain.Num.Fmt(E.StandardWeight(x.N(Keys.Height))), _ => 9),
                new("肥満度", "%", "", x => x == null ? "" : Domain.Num.Fmt(E.Obesity(x.N(Keys.Height), x.N(Keys.Weight))), _ => 9),
                new("BMI", "", bmiTier != null ? $"{bmiTier.Min}〜{bmiTier.Max}" : "18.5〜24.9", x => x == null ? "" : Domain.Num.Fmt(JudgeEngine.Bmi(x.N(Keys.Height), x.N(Keys.Weight))), x => CmpRange(x == null ? null : JudgeEngine.Bmi(x.N(Keys.Height), x.N(Keys.Weight)), bmiTier?.Min, bmiTier?.Max)),
                new("腹囲", "cm", $"{waistLimit:F1}未満", x => x?.V(Keys.Waist) ?? "", x => x?.N(Keys.Waist) is { } w ? (w >= waistLimit ? 1 : 0) : 9),
            }),
            new("視力", Sections.Vision, new()
            {
                new("右 裸眼（矯正）", "", $"{C.Vision.NormalMin}以上", x => Vis(x, Keys.VisionR, Keys.VisionRc), x => VisCmp(x, Keys.VisionR, Keys.VisionRc)),
                new("左 裸眼（矯正）", "", $"{C.Vision.NormalMin}以上", x => Vis(x, Keys.VisionL, Keys.VisionLc), x => VisCmp(x, Keys.VisionL, Keys.VisionLc)),
            }),
            new("聴力", Sections.Hearing, new()
            {
                new("1000Hz（右／左）", "", "所見なし", x => Hear(x, Keys.HearR1000, Keys.HearL1000), x => HearCmp(x, Keys.HearR1000, Keys.HearL1000)),
                new("4000Hz（右／左）", "", "所見なし", x => Hear(x, Keys.HearR4000, Keys.HearL4000), x => HearCmp(x, Keys.HearR4000, Keys.HearL4000)),
            }),
            new("尿検査", Sections.Urine, new()
            {
                new("蛋白", "", "（−）", x => Q(x?.V(Keys.UrineProtein)), x => QCmp(x?.V(Keys.UrineProtein))),
                new("潜血", "", "（−）", x => Q(x?.V(Keys.UrineBlood)), x => QCmp(x?.V(Keys.UrineBlood))),
            }),
        };
        var bp0 = C.BloodPressure.Tiers.FirstOrDefault();
        var bpRef = bp0 != null ? $"{bp0.SysMax + 1}未満 / {bp0.DiaMax + 1}未満" : "";
        g.Add(new("血圧", Sections.Bp, new()
        {
            new("1回目（最高／最低）", "mmHg", bpRef, x => Bp(x, Keys.Bp1Sys, Keys.Bp1Dia), x => BpCmp(x, Keys.Bp1Sys, Keys.Bp1Dia, bp0)),
            new("2回目（最高／最低）", "mmHg", bpRef, x => Bp(x, Keys.Bp2Sys, Keys.Bp2Dia), x => BpCmp(x, Keys.Bp2Sys, Keys.Bp2Dia, bp0)),
            new("判定に使う値", "mmHg", bpRef, x => x == null ? "" : E.BpToUse(x) is { } b ? $"{b.sys} / {b.dia}" : "", x => x == null ? 9 : E.BpToUse(x) is { } b ? (bp0 != null && (b.sys > bp0.SysMax || b.dia > bp0.DiaMax) ? 1 : 0) : 9),
        }));
        // 血液：共有された書式の並び
        NRow B(string item, string label) { var m = M.BloodItems.FirstOrDefault(b => b.Item == item); return new(label, m?.Unit ?? "", m?.RefFor(sex) ?? "", x => BloodVal(x, item), x => BloodCmp(x, item, sex)); }
        g.Add(new("血中脂質", Sections.BloodPrefix + "脂質", new() { B("総コレステロール", "総コレステロール"), B("中性脂肪（TG）", "中性脂肪"), B("HDLコレステロール", "HDLコレステロール"), B("LDLコレステロール（計算法）", "LDLコレステロール") }));
        g.Add(new("糖代謝", Sections.BloodPrefix + "糖機能", new() { new("尿糖", "", "（−）", x => Q(x?.V(Keys.UrineSugar)), x => QCmp(x?.V(Keys.UrineSugar))), B("血糖", "血糖"), B("HbA1c（NGSP）", "HbA1c（NGSP）") }));
        g.Add(new("肝機能", Sections.BloodPrefix + "肝機能", new() { B("AST（GOT）", "AST（GOT）"), B("ALT（GPT）", "ALT（GPT）"), B("γ-GT（γGTP）", "γ-GT（γ-GTP）"), B("ALP（IFCC）", "ALP"), B("LD（IFCC）", "LD（LDH）"), B("総蛋白", "総蛋白"), B("アルブミン", "アルブミン"), B("総ビリルビン", "総ビリルビン"), B("コリンエステラーゼ", "CHE"), new("尿ウロビリノーゲン", "", "正常", x => x?.V(Keys.UrineUrobil) ?? "", x => x?.V(Keys.UrineUrobil) is { Length: > 0 } u ? (u == "正常" ? 0 : 1) : 9) }));
        g.Add(new("血液一般", Sections.BloodPrefix + "血液一般", new() { B("ヘモグロビン量", "ヘモグロビン"), B("ヘマトクリット値", "ヘマトクリット"), B("赤血球数", "赤血球数"), B("白血球数", "白血球数"), B("血小板数", "血小板"), B("MCV", "MCV"), B("MCH", "MCH"), B("MCHC", "MCHC") }));
        g.Add(new("腎機能", Sections.BloodPrefix + "腎機能", new() { B("尿素窒素", "BUN"), B("クレアチニン", "CRE"), B("eGFR", "eGFR"), B("尿酸", "UA（尿酸）") }));
        g.Add(new("膵機能", Sections.BloodPrefix + "膵機能", new() { B("アミラーゼ", "AMY（アミラーゼ）") }));
        g.Add(new("血清", Sections.BloodPrefix + "血清", new() { B("CRP定量", "CRP"), B("RF定量", "RF"), B("HBs抗原（精密測定）", "HBs抗原"), B("HCV抗体（CLIA法）", "HCV抗体"), B("RPR定性", "RPR") }));
        // コースに無い群（値も判定も無い）は外す
        return g.Where(gr => gr.Rows.Any(r => r.Val(now) != "") || (gr.Section != "" && _d.JudgCode(now, gr.Section) != "")).ToList();
    }

    static string Vis(ExamContext? x, string k, string kc) { if (x == null) return ""; var n = x.V(k); var c = x.V(kc); return c != "" ? $"{n}（{c}）" : n; }
    int VisCmp(ExamContext? x, string k, string kc) { if (x == null) return 9; var v = x.N(kc) ?? x.N(k); return v == null ? 9 : v < _d.C.Vision.NormalMin ? -1 : 0; }
    static string Hear(ExamContext? x, string r, string l) { if (x == null) return ""; var a = x.V(r); var b = x.V(l); return a == "" && b == "" ? "" : $"{H(a)} / {H(b)}"; static string H(string v) => v == "なし" ? "所見なし" : v == "あり" ? "所見あり" : v; }
    static int HearCmp(ExamContext? x, string r, string l) { if (x == null) return 9; var a = x.V(r); var b = x.V(l); return a == "" && b == "" ? 9 : a == "あり" || b == "あり" ? 1 : 0; }
    static string Q(string? v) => string.IsNullOrEmpty(v) ? "" : "（" + v + "）";
    static int QCmp(string? v) => string.IsNullOrEmpty(v) ? 9 : v == "−" || v == "-" || v == "正常" ? 0 : 1;
    static string Bp(ExamContext? x, string s, string d) { if (x == null) return ""; var a = x.V(s); var b = x.V(d); return a == "" && b == "" ? "" : $"{a} / {b}"; }
    static int BpCmp(ExamContext? x, string s, string d, BpTier? t) { if (x == null || t == null) return 9; var a = x.N(s); var b = x.N(d); if (a == null && b == null) return 9; return (a != null && a > t.SysMax) || (b != null && b > t.DiaMax) ? 1 : 0; }
    static string BloodVal(ExamContext? x, string item) => x?.Blood.FirstOrDefault(b => b.Item == item)?.Value ?? "";
    int BloodCmp(ExamContext? x, string item, string sex)
    {
        var b = x?.Blood.FirstOrDefault(r => r.Item == item); if (b == null) return 9;
        if (b.Flag == "↑") return 1; if (b.Flag == "↓") return -1;
        var v = b.NumericValue; if (v == null) return b.Value.Contains("陽") || b.Value.Contains("+") ? 1 : 0;
        var lo = b.RefLow; var hi = b.RefHigh;
        if (lo == null && hi == null) { var m = _d.M.BloodItems.FirstOrDefault(i => i.Item == item); if (m != null) (lo, hi) = Lab.FalcoPdfReader.ParseRange(m.RefFor(sex)); }
        return hi != null && v > hi ? 1 : lo != null && v < lo ? -1 : 0;
    }

    // ---------- グラフ ----------
    void Charts(ColumnDescriptor col)
    {
        var xs = new[] { _d.Prev2, _d.Prev1, _d.Now };
        var labels = new[] { "前々回", "前回", "今回" };
        void Chart(string title, string unit, Func<ExamContext?, double?> f, Func<ExamContext?, double?>? f2 = null)
        {
            var v1 = xs.Select(f).ToArray(); var v2 = f2 == null ? null : xs.Select(f2).ToArray();
            col.Item().PaddingBottom(3).Column(c =>
            {
                c.Item().Background(Band).PaddingVertical(1).AlignCenter().Text(title + (unit != "" ? $"　単位：{unit}" : "")).FontColor(Colors.White).FontSize(7).Bold();
                c.Item().Border(0.4f).BorderColor(Colors.Grey.Medium).Height(62).Svg(LineChart(v1, v2, labels));
            });
        }
        double? BV(ExamContext? x, string item) => x?.Blood.FirstOrDefault(b => b.Item == item)?.NumericValue;
        Chart("腹囲", "cm", x => x?.N(Keys.Waist));
        Chart("血圧（■最高 ●最低）", "mmHg", x => x == null ? null : _d.E.BpToUse(x)?.sys, x => x == null ? null : _d.E.BpToUse(x)?.dia);
        Chart("LDLコレステロール", "mg/dL", x => BV(x, "LDLコレステロール（計算法）"));
        Chart("中性脂肪", "mg/dL", x => BV(x, "中性脂肪（TG）"));
        Chart("血糖", "mg/dL", x => BV(x, "血糖"));
        Chart("HbA1c（NGSP）", "%", x => BV(x, "HbA1c（NGSP）"));
    }

    /// <summary>3 点の折れ線（SVG）。値が無い回は飛ばす。</summary>
    static string LineChart(double?[] v1, double?[]? v2, string[] labels)
    {
        const double W = 200, H = 62, L = 30, R = 8, T = 8, B = 14;
        var all = v1.Concat(v2 ?? Array.Empty<double?>()).Where(v => v != null).Select(v => v!.Value).ToList();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} {H}' width='{W}' height='{H}'>");
        var xs = new[] { L + 15, L + (W - L - R) / 2, W - R - 15 };
        double lo, hi;
        if (all.Count == 0) { lo = 0; hi = 1; }
        else { lo = all.Min(); hi = all.Max(); var pad = Math.Max((hi - lo) * 0.25, Math.Max(hi * 0.05, 1)); lo -= pad; hi += pad; if (lo < 0 && all.Min() >= 0) lo = 0; }
        double Y(double v) => T + (H - T - B) * (1 - (v - lo) / (hi - lo));
        // 目盛り 3 本
        for (var i = 0; i <= 2; i++)
        {
            var val = lo + (hi - lo) * i / 2; var y = Y(val);
            sb.Append(CultureInfo.InvariantCulture, $"<line x1='{L}' y1='{y:F1}' x2='{W - R}' y2='{y:F1}' stroke='#ddd' stroke-width='0.5'/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x='{L - 2}' y='{y + 2.5:F1}' font-size='6' text-anchor='end' fill='#555'>{val:0.#}</text>");
        }
        for (var i = 0; i < 3; i++) sb.Append(CultureInfo.InvariantCulture, $"<text x='{xs[i]}' y='{H - 3}' font-size='6.5' text-anchor='middle' fill='#333'>{labels[i]}</text>");
        void Series(double?[] v, string color, bool square)
        {
            var pts = Enumerable.Range(0, 3).Where(i => v[i] != null).Select(i => (x: xs[i], y: Y(v[i]!.Value))).ToList();
            if (pts.Count >= 2) sb.Append(CultureInfo.InvariantCulture, $"<polyline points='{string.Join(" ", pts.Select(p => $"{p.x:F1},{p.y:F1}"))}' fill='none' stroke='{color}' stroke-width='1.2'/>");
            foreach (var p in pts)
            {
                if (square) sb.Append(CultureInfo.InvariantCulture, $"<rect x='{p.x - 2.2:F1}' y='{p.y - 2.2:F1}' width='4.4' height='4.4' fill='{color}'/>");
                else sb.Append(CultureInfo.InvariantCulture, $"<circle cx='{p.x:F1}' cy='{p.y:F1}' r='2.2' fill='{color}'/>");
            }
        }
        Series(v1, "#333", true);
        if (v2 != null) Series(v2, "#2a7fc1", false);
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ---------- 2 ページ目：所見・問診・総合判定 ----------
    static readonly (string section, string label, string about)[] FindingBlocks =
    {
        (Sections.ChestXray, "胸部X線", "胸部（心臓を含む）に関する疾患を調べることができます。"),
        (Sections.Ecg, "心電図", "心筋の動きを電気的に記録したもので、心筋梗塞、狭心症、不整脈などの診断には欠かせない検査です。"),
        (Sections.Gastroscopy, "上部消化管（胃内視鏡）", "食道・胃・十二指腸の一部を写し出して観察します。腫瘍の変化や炎症がわかります。"),
        (Sections.StomachXray, "胃部X線", "バリウムを飲んで食道・胃・十二指腸の形の変化を写します。"),
        (Sections.Colonoscopy, "大腸内視鏡", "大腸の内側を観察し、ポリープや炎症を調べます。"),
        (Sections.Echo, "腹部超音波", "臓器内部を超音波で写し出すことで、結石や腫瘍、脂肪肝などを調べます。"),
        (Sections.Physical, "内科診察", "医師の診察による所見です。"),
    };

    void FindingsAndComments(IDocumentContainer container) => Page(container, page =>
    {
        var now = _d.Now; var E = _d.E; var M = _d.M;
        var xs = new[] { _d.Now, _d.Prev1, _d.Prev2 };
        var course = M.CourseOf(now.Exam.Course);
        page.Content().Column(col =>
        {
            col.Item().Text($"{now.Patient.Name}　様　　受診日 {now.Exam.ExamDate.Replace('-', '/')}").FontSize(8);
            col.Item().PaddingTop(3).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(16); cd.RelativeColumn(); cd.ConstantColumn(34); cd.RelativeColumn(); cd.ConstantColumn(34); cd.RelativeColumn(); cd.ConstantColumn(34); cd.RelativeColumn(1.3f); });
                t.Cell().Element(c => Cell(c, Head, true)).Text("");
                foreach (var (x, lab) in xs.Zip(new[] { "今回", "前回", "前々回" })) { t.Cell().Element(c => Cell(c, Head, true)).Text($"{lab}　{ReportData.Date(x)}"); t.Cell().Element(c => Cell(c, Head, true)).Text("判定"); }
                t.Cell().Element(c => Cell(c, Head, true)).Text("検査の説明");
                foreach (var (section, label, about) in FindingBlocks)
                {
                    var inCourse = course?.Sections.Contains(section) ?? true;
                    var any = xs.Any(x => x != null && (x.Findings.Any(f => f.Section == section) || x.Existing.Any(j => j.Section == section)));
                    if (!inCourse && !any) continue;
                    t.Cell().Element(c => c.Border(0.4f).BorderColor(Colors.Grey.Medium).Background(Band).Padding(1).AlignMiddle().AlignCenter()).Text(Vertical(label.Length > 6 ? label[..6] : label)).FontColor(Colors.White).FontSize(6.5f);
                    foreach (var x in xs)
                    {
                        var has = x != null && (x.Findings.Any(f => f.Section == section) || x.Existing.Any(j => j.Section == section));
                        var text = x == null ? "" : has ? (x.Findings.Any(f => f.Section == section) ? _d.Findings(x, section) : "") : (x == _d.Now ? "今回は受診されていません" : "受診されていません");
                        t.Cell().Element(c => Cell(c, x == _d.Now ? Now : null)).MinHeight(34).Text(text).FontSize(has ? 7.5f : 6.5f).FontColor(has ? Colors.Black : Colors.Grey.Darken1);
                        t.Cell().Element(c => Cell(c, x == _d.Now ? Now : null, true)).Text(_d.Judg(x, section)).FontSize(7f).Bold();
                    }
                    t.Cell().Element(c => Cell(c)).Text(about).FontSize(6.5f);
                }
                // 便潜血
                t.Cell().Element(c => c.Border(0.4f).BorderColor(Colors.Grey.Medium).Background(Band).Padding(1).AlignMiddle().AlignCenter()).Text(Vertical("便潜血")).FontColor(Colors.White).FontSize(6.5f);
                foreach (var x in xs)
                {
                    var s1 = x?.V(Keys.Stool1) ?? ""; var s2 = x?.V(Keys.Stool2) ?? "";
                    t.Cell().Element(c => Cell(c, x == _d.Now ? Now : null)).Text(s1 == "" && s2 == "" ? "" : $"1日目（{s1}）　2日目（{s2}）").FontSize(7.5f);
                    t.Cell().Element(c => Cell(c, x == _d.Now ? Now : null, true)).Text(_d.Judg(x, Sections.Stool)).FontSize(7f).Bold();
                }
                t.Cell().Element(c => Cell(c)).Text("下部消化管出血の有無を調べます。大腸疾患の早期発見の目安となります。").FontSize(6.5f);
            });
            // 問診内容・医師
            col.Item().PaddingTop(4).Row(r =>
            {
                r.RelativeItem(3).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(16); cd.ConstantColumn(44); cd.RelativeColumn(); });
                    t.Cell().RowSpan(7).Element(c => c.Border(0.4f).BorderColor(Colors.Grey.Medium).Background(Band).Padding(1).AlignMiddle().AlignCenter()).Text(Vertical("問診内容")).FontColor(Colors.White).FontSize(6.5f);
                    void R(string a, string b) { t.Cell().Element(c => Cell(c, Head)).Text(a); t.Cell().Element(c => Cell(c)).Text(b); }
                    R("既往歴", Or(now.V(Keys.QHistory), "特記事項なし"));
                    R("治療歴（服薬）", string.Join("　", new[] { (Keys.QBpMed, "血圧"), (Keys.QDmMed, "血糖"), (Keys.QLipidMed, "脂質") }.Where(k => now.V(k.Item1) == "1").Select(k => k.Item2 + "の薬")) is { Length: > 0 } med ? med : "特記事項なし");
                    R("自覚症状", Or(now.V(Keys.QSymptoms), "特になし"));
                    R("食後時間", now.V(Keys.MealMinutes) is { Length: > 0 } mm && double.TryParse(mm, out var mi) ? $"{mi / 60:0.#} 時間" : "");
                    R("生活習慣", $"飲酒：{Or(now.V(Keys.QAlcoholFreq), "—")}（{Or(now.V(Keys.QAlcoholAmount), "—")}）　たばこ：{(now.V(Keys.QSmoking) == "1" ? "はい" : "いいえ")}　運動：{(now.V(Keys.QExercise) == "1" ? "週2回以上" : "いいえ")}");
                    R("体重の変化", now.V(Keys.QWeightGain) == "1" ? "20歳から10kg以上増加" : "20歳から10kg以上の増加なし");
                    R("次回のカメラ", $"胃 {Or(now.V(Keys.NextGastroYears), "—")} 年後　大腸 {Or(now.V(Keys.NextColonYears), "—")} 年後");
                });
                r.ConstantItem(5);
                r.RelativeItem(1).Column(c =>
                {
                    c.Item().Border(0.6f).BorderColor(Colors.Grey.Darken1).Padding(4).Column(cc => { cc.Item().Text("総合判定医師").FontSize(6.5f).FontColor(Colors.Grey.Darken2); cc.Item().Text(_d.DoctorName).FontSize(11); cc.Item().Text(_d.State.Config.Clinic.Name).FontSize(6.5f); });
                });
            });
            // 総合判定
            var overall = _d.Judg(now, Sections.Overall);
            var h = M.Comment(now.V(Keys.CommentH))?.Text ?? ""; var c2 = M.Comment(now.V(Keys.CommentC))?.Text ?? "";
            var lines = new[] { "今回受診された健康診断の結果につき、次の通りご報告いたします。", h, c2, now.V(Keys.FreeComment) }.Where(s => s != "").ToList();
            col.Item().PaddingTop(4).Row(r =>
            {
                r.ConstantItem(16).Element(c => c.Border(0.4f).BorderColor(Colors.Grey.Medium).Background(Band).Padding(1).AlignMiddle().AlignCenter()).Text(Vertical("総合判定")).FontColor(Colors.White).FontSize(6.5f);
                r.RelativeItem().Border(0.4f).BorderColor(Colors.Grey.Medium).Padding(5).MinHeight(80).Column(c =>
                {
                    foreach (var l in lines) c.Item().PaddingBottom(2).Text(l).FontSize(8.5f);
                });
                r.ConstantItem(90).Border(0.6f).BorderColor(Colors.Red.Darken1).Padding(4).AlignMiddle().AlignCenter().Column(c =>
                {
                    c.Item().AlignCenter().Text("総合判定").FontSize(6.5f).FontColor(Colors.Red.Darken1);
                    c.Item().AlignCenter().Text(overall == "" ? "—" : overall).FontSize(overall.Length > 4 ? 13 : 18).Bold().FontColor(Colors.Red.Darken1);
                });
            });
            var met = E.JudgeMetabolic(now);
            var strat = E.JudgeStratification(now);
            col.Item().PaddingTop(3).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(90); cd.RelativeColumn(); cd.ConstantColumn(60); cd.RelativeColumn(); });
                t.Cell().Element(c => Cell(c, Head)).Text("メタボリックシンドローム判定"); t.Cell().Element(c => Cell(c)).Text(met.Label);
                t.Cell().Element(c => Cell(c, Head)).Text("階層化判定"); t.Cell().Element(c => Cell(c)).Text(strat.label);
                t.Cell().Element(c => Cell(c, Head)).Text("メタボのコメント"); t.Cell().ColumnSpan(3).Element(c => Cell(c)).Text(met.Comment).FontSize(6.8f);
            });
        });
    });

    static string Or(string v, string alt) => string.IsNullOrWhiteSpace(v) ? alt : v;

    // ---------- 3 ページ目：表紙 ----------
    void Cover(IDocumentContainer container) => Page(container, page =>
    {
        var p = _d.Now.Patient; var e = _d.Now.Exam;
        page.Content().Column(col =>
        {
            col.Item().PaddingTop(20).Width(300).Border(0.6f).Padding(8).Column(c =>
            {
                c.Item().Text(p.Office).FontSize(9);
                c.Item().PaddingTop(4).Text($"{p.Name}　様").FontSize(16);
                c.Item().Text(p.Kana).FontSize(8).FontColor(Colors.Grey.Darken1);
            });
            col.Item().PaddingTop(120).AlignCenter().Column(c =>
            {
                c.Item().AlignCenter().Background(Band).Padding(8).Width(220).AlignCenter().Text(e.Course.Contains("ドック") ? "人間ドック" : "健康診断").FontSize(20).FontColor(Colors.White);
                c.Item().PaddingTop(10).AlignCenter().Text("検査結果のお知らせ").FontSize(26).Bold().FontColor(Band);
                c.Item().PaddingTop(14).AlignCenter().Text("健康診断の受診お疲れさまでした。結果をご報告いたします。\nあなたの健康で豊かな毎日のために、ぜひご活用ください。").FontSize(9).LineHeight(1.6f);
            });
            col.Item().PaddingTop(60).AlignCenter().Width(260).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(120); cd.RelativeColumn(); });
                void R(string a, string b) { t.Cell().Element(c => Cell(c, Head)).Text(a); t.Cell().Element(c => Cell(c)).Text(b); }
                R("今回健診実施日", Fmt(_d.Now.Exam.ExamDate)); R("前回健診実施日", Fmt(_d.Prev1?.Exam.ExamDate)); R("前々回健診実施日", Fmt(_d.Prev2?.Exam.ExamDate));
            });
            col.Item().PaddingTop(60).AlignCenter().Width(340).Border(0.6f).Padding(8).Column(c =>
            {
                c.Item().Row(r => { r.RelativeItem().Text("健康診断実施医師名").FontSize(7); r.RelativeItem().Text("産業医名").FontSize(7); });
                c.Item().Row(r => { r.RelativeItem().Text(_d.DoctorName).FontSize(11); r.RelativeItem().Text(_d.State.Config.Clinic.OccupationalDoctor).FontSize(11); });
                c.Item().PaddingTop(6).AlignCenter().Text(_d.State.Config.Clinic.Name).FontSize(11);
            });
        });
    });

    static string Fmt(string? ymd) => string.IsNullOrEmpty(ymd) ? "" : DateOnly.TryParse(ymd, out var d) ? $"{d.Year}年{d.Month}月{d.Day}日" : ymd;
}
