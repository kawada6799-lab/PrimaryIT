using Kenshin.Config;
using Kenshin.Domain;
using Kenshin.Judge;
using Kenshin.Web;
using QRCoder;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kenshin.Reports;

/// <summary>1 回分の帳票に使う材料（今回・前回・前々回）。</summary>
public sealed class ReportData
{
    public required AppState State { get; init; }
    public required ExamContext Now { get; init; }
    public ExamContext? Prev1 { get; init; }
    public ExamContext? Prev2 { get; init; }
    public Masters M => State.Masters;
    public Criteria C => State.Criteria;
    public JudgeEngine E => State.Engine;

    public string Judg(ExamContext? x, string section)
    {
        var j = x?.Existing.FirstOrDefault(k => k.Section == section);
        return j == null ? "" : C.Label(M, section, j.Code);
    }
    public string JudgCode(ExamContext? x, string section) => x?.Existing.FirstOrDefault(k => k.Section == section)?.Code ?? "";
    public static string V(ExamContext? x, string key) => x?.V(key) ?? "";
    public static string Blood(ExamContext? x, string itemContains)
    {
        var b = x?.Blood.FirstOrDefault(r => r.Item.Contains(itemContains, StringComparison.OrdinalIgnoreCase));
        return b == null ? "" : (b.Flag + " " + b.Value).Trim();
    }
    public string Findings(ExamContext? x, string section) => x == null ? "" : string.Join("\n", x.Findings.Where(f => f.Section == section).Select(f => f.Text));
    public string DoctorName => string.IsNullOrEmpty(Now.V(Keys.DoctorName)) ? State.Config.Clinic.Doctor : Now.V(Keys.DoctorName);
    public static string Date(ExamContext? x) => x == null ? "" : x.Exam.ExamDate.Replace('-', '.');
}

public static class ReportService
{
    static bool _init;
    static readonly object InitLock = new();
    public const string Font = "IPAPGothic";

    public static void Init(string baseDir)
    {
        lock (InitLock)
        {
            if (_init) return;
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
            var font = Path.Combine(baseDir, "fonts", "ipagp.ttf");
            if (File.Exists(font)) using (var fs = File.OpenRead(font)) FontManager.RegisterFontFromStream(fs);
            _init = true;
        }
    }

    public static ReportData Gather(AppState state, ExamService svc, long examId)
    {
        var now = svc.Context(examId) ?? throw new InvalidOperationException("健診回がありません");
        var prev = state.RequireDb().ExamsOfPatient(now.Patient.Id).Where(e => e.Id != examId && string.CompareOrdinal(e.ExamDate, now.Exam.ExamDate) < 0).Take(2).ToList();
        return new ReportData { State = state, Now = now, Prev1 = prev.Count > 0 ? svc.Context(prev[0].Id) : null, Prev2 = prev.Count > 1 ? svc.Context(prev[1].Id) : null };
    }

    public static byte[] Render(AppState state, ExamService svc, long examId, string kind)
    {
        Init(state.Config.BaseDir);
        var d = Gather(state, svc, examId);
        IDocument doc = kind switch
        {
            "jushinhyo" => new JushinhyoDocument(d),
            "notice" => new NoticeDocument(d),
            "explanation" => new ExplanationDocument(d),
            _ => throw new ArgumentException("kind"),
        };
        return doc.GeneratePdf();
    }

    public static string SaveNotice(AppState state, ExamService svc, long examId)
    {
        var bytes = Render(state, svc, examId, "notice");
        var dir = state.PdfDir; Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{examId}_{DateTime.Now:yyyyMMdd_HHmmss}_結果のお知らせ.pdf");
        File.WriteAllBytes(path, bytes);
        state.RequireDb().AddIssuedPdf(examId, "結果のお知らせ", path);
        return path;
    }

    public static void Map(WebApplication app, AppState state, ExamService svc)
    {
        app.MapGet("/api/exams/{id:long}/pdf/{kind}", (long id, string kind, bool? download) =>
        {
            if (kind is not ("jushinhyo" or "notice" or "explanation")) return Results.NotFound();
            byte[] bytes;
            try { bytes = Render(state, svc, id, kind); }
            catch (InvalidOperationException) { return Results.NotFound(); }
            var name = kind switch { "jushinhyo" => "受診票", "notice" => "結果のお知らせ", _ => "検査内容の説明" };
            state.RequireDb().Log("印刷（" + name + "）", id, "");
            if (download == true) return Results.File(bytes, "application/pdf", $"{id}_{name}.pdf");
            return Results.File(bytes, "application/pdf");
        });
        app.MapGet("/api/exams/{id:long}/issued/{pdfId:long}", (long id, long pdfId) =>
        {
            var p = state.RequireDb().IssuedPdfs(id).FirstOrDefault(x => x.Id == pdfId);
            return p != null && File.Exists(p.Path) ? Results.File(p.Path, "application/pdf") : Results.NotFound();
        });
    }

    // ---------- 共通部品 ----------
    public static byte[] Qr(string payload)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(6);
    }

    public static string Wareki(string ymd)
    {
        if (!DateOnly.TryParse(ymd, out var d)) return ymd;
        var (era, start) = d >= new DateOnly(2019, 5, 1) ? ("令和", 2018) : d >= new DateOnly(1989, 1, 8) ? ("平成", 1988) : d >= new DateOnly(1926, 12, 25) ? ("昭和", 1925) : ("大正", 1911);
        var y = d.Year - start;
        return $"{era}{(y == 1 ? "元" : y.ToString())}年{d.Month}月{d.Day}日";
    }

    public static IContainer Cell(IContainer c, bool head = false, bool center = false) =>
        c.Border(0.5f).BorderColor(Colors.Grey.Darken1).Background(head ? Colors.Grey.Lighten3 : Colors.White).PaddingVertical(1.5f).PaddingHorizontal(3).AlignMiddle().Element(x => center ? x.AlignCenter() : x);
}

/// <summary>受診票（A4）。今の紙と同じ並びで、QR 付き。</summary>
public sealed class JushinhyoDocument : IDocument
{
    readonly ReportData _d;
    public JushinhyoDocument(ReportData d) { _d = d; }
    public DocumentMetadata GetMetadata() => new() { Title = "健康診断受診票" };

    public void Compose(IDocumentContainer container)
    {
        var x = _d.Now; var p = x.Patient; var e = x.Exam; var m = _d.M;
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(12, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontFamily(ReportService.Font).FontSize(8.5f));
            page.Header().Row(r =>
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text("健康診断受診票").FontSize(18).Bold();
                    c.Item().Text(_d.State.Config.Clinic.Name).FontSize(9);
                });
                r.ConstantItem(220).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(44); cd.RelativeColumn(); cd.ConstantColumn(34); cd.RelativeColumn(); });
                    void H(string s) => t.Cell().Element(c => ReportService.Cell(c, true)).Text(s);
                    void D(string s) => t.Cell().Element(c => ReportService.Cell(c)).Text(s);
                    H("事業所"); t.Cell().ColumnSpan(3).Element(c => ReportService.Cell(c)).Text(p.Office);
                    H("個人ID"); D(p.FalcoId != "" ? p.FalcoId : p.ChartNo); H("氏名"); D(p.Name);
                    H("生年月日"); D($"{ReportService.Wareki(p.BirthDate)}（{p.AgeAt(DateOnly.TryParse(e.ExamDate, out var dd) ? dd : DateOnly.FromDateTime(DateTime.Today))}才）"); H("カナ"); D(p.Kana);
                    H("性別"); D(p.Sex); H("実施日"); D(e.ExamDate.Replace('-', '/'));
                });
                r.ConstantItem(62).AlignRight().Width(56).Image(ReportService.Qr($"kenshin:exam:{e.Id}"));
            });
            page.Content().PaddingTop(6).Row(row =>
            {
                row.RelativeItem(5).Column(col => LeftTable(col, x, m));
                row.ConstantItem(6);
                row.RelativeItem(4).Column(col => RightTable(col, x, m));
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"個人ID {p.FalcoId}  {p.Name}  {e.ExamDate}  ／ 健康診断実施医師名：{_d.DoctorName}　産業医名：{_d.State.Config.Clinic.OccupationalDoctor}").FontSize(7.5f);
                r.ConstantItem(90).AlignRight().Text(t => { t.Span("印刷 " + DateTime.Now.ToString("yyyy/MM/dd HH:mm")).FontSize(7); });
            });
        });
    }

    void LeftTable(ColumnDescriptor col, ExamContext x, Masters m)
    {
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.ConstantColumn(40); cd.ConstantColumn(60); cd.RelativeColumn(); cd.ConstantColumn(58); });
            void Head(string a, string b, string c, string d) { foreach (var s in new[] { a, b, c, d }) t.Cell().Element(z => ReportService.Cell(z, true, true)).Text(s); }
            Head("", "項目", "今回値", "判定");
            void Row(string section, string item, string value, bool first, int rows)
            {
                if (first) t.Cell().RowSpan((uint)rows).Element(z => ReportService.Cell(z, true, true)).Text(section);
                t.Cell().Element(z => ReportService.Cell(z)).Text(item);
                t.Cell().Element(z => ReportService.Cell(z)).Text(value);
                if (first) t.Cell().RowSpan((uint)rows).Element(z => ReportService.Cell(z, false, true)).Text(JudgText(x, section));
            }
            var bmi = JudgeEngine.Bmi(x.N(Keys.Height), x.N(Keys.Weight));
            Row(Sections.Measure, "身長", x.V(Keys.Height) + " cm", true, 4); Row(Sections.Measure, "体重", x.V(Keys.Weight) + " kg", false, 0);
            Row(Sections.Measure, "腹囲", x.V(Keys.Waist) + " cm", false, 0); Row(Sections.Measure, "BMI", Num.Fmt(bmi), false, 0);
            Row(Sections.Vision, "右／左", $"{x.V(Keys.VisionR)} ／ {x.V(Keys.VisionL)}", true, 2); Row(Sections.Vision, "矯正", $"{x.V(Keys.VisionRc)} ／ {x.V(Keys.VisionLc)}", false, 0);
            Row(Sections.Hearing, "右 1000/4000Hz", $"{x.V(Keys.HearR1000)} ／ {x.V(Keys.HearR4000)}", true, 3); Row(Sections.Hearing, "左 1000/4000Hz", $"{x.V(Keys.HearL1000)} ／ {x.V(Keys.HearL4000)}", false, 0); Row(Sections.Hearing, "会話法", x.V(Keys.HearConv), false, 0);
            Row(Sections.Bp, "1 回目", $"{x.V(Keys.Bp1Sys)} ／ {x.V(Keys.Bp1Dia)} mmHg", true, 3); Row(Sections.Bp, "2 回目", $"{x.V(Keys.Bp2Sys)} ／ {x.V(Keys.Bp2Dia)} mmHg", false, 0); Row(Sections.Bp, "降圧剤服用", x.V(Keys.BpMed) == "1" ? "あり" : "なし", false, 0);
            Row(Sections.Urine, "糖", x.V(Keys.UrineSugar), true, 4); Row(Sections.Urine, "蛋白", x.V(Keys.UrineProtein), false, 0); Row(Sections.Urine, "潜血", x.V(Keys.UrineBlood), false, 0); Row(Sections.Urine, "ｳﾛﾋﾞﾘﾉｰｹﾞﾝ", x.V(Keys.UrineUrobil) + (x.V(Keys.UrineMenstruating) == "1" ? "（生理中）" : ""), false, 0);
            Row(Sections.Stool, "潜血 1 回目", x.V(Keys.Stool1), true, 2); Row(Sections.Stool, "2 回目", x.V(Keys.Stool2), false, 0);
            Row(Sections.Lung, "肺活量／1 秒率", $"{x.V(Keys.LungVc)} ml ／ {x.V(Keys.LungFev1Pct)} %", true, 1);
            t.Cell().ColumnSpan(2).Element(z => ReportService.Cell(z, true)).Text("食後時間");
            t.Cell().ColumnSpan(2).Element(z => ReportService.Cell(z)).Text(x.V(Keys.MealMinutes) == "" ? "" : x.V(Keys.MealMinutes) + " 分");
        });
        col.Item().PaddingTop(6).Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.ConstantColumn(60); cd.RelativeColumn(); cd.ConstantColumn(58); });
            t.Cell().ColumnSpan(3).Element(z => ReportService.Cell(z, true, true)).Text("血液検査判定");
            foreach (var cat in _d.C.Blood.JudgedCategories)
            {
                var sec = Sections.BloodPrefix + cat;
                var items = x.Blood.Where(b => b.Category == cat && _d.E.IsOutOfRange(b)).Select(b => $"{b.Item} {b.Value}{b.Flag}");
                t.Cell().Element(z => ReportService.Cell(z, true)).Text(cat);
                t.Cell().Element(z => ReportService.Cell(z)).Text(string.Join("、", items));
                t.Cell().Element(z => ReportService.Cell(z, false, true)).Text(JudgText(x, sec));
            }
            t.Cell().Element(z => ReportService.Cell(z, true)).Text("総合判定");
            t.Cell().ColumnSpan(2).Element(z => ReportService.Cell(z)).Text(JudgText(x, Sections.Overall) + "　" + JudgCodeText(x, Sections.Overall));
            t.Cell().Element(z => ReportService.Cell(z, true)).Text("判定コメント");
            t.Cell().ColumnSpan(2).Element(z => ReportService.Cell(z)).Text($"{x.V(Keys.CommentH)} {m.Comment(x.V(Keys.CommentH))?.Text}\n{x.V(Keys.CommentC)} {m.Comment(x.V(Keys.CommentC))?.Text}\n{x.V(Keys.FreeComment)}".Trim());
            t.Cell().Element(z => ReportService.Cell(z, true)).Text("メタボ判定");
            t.Cell().ColumnSpan(2).Element(z => ReportService.Cell(z)).Text(_d.E.JudgeMetabolic(x).Label);
        });
    }

    void RightTable(ColumnDescriptor col, ExamContext x, Masters m)
    {
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.ConstantColumn(46); cd.RelativeColumn(); cd.ConstantColumn(58); });
            foreach (var s in new[] { "項目", "受診日・所見", "判定" }) t.Cell().Element(z => ReportService.Cell(z, true, true)).Text(s);
            foreach (var section in Sections.FindingSections)
            {
                var fs = x.Findings.Where(f => f.Section == section).ToList();
                var date = x.V(Keys.SectionDate(section));
                var extra = section == Sections.ChestXray ? "（" + (x.V(Keys.XrayDirect) == "" ? "直接" : x.V(Keys.XrayDirect)) + "）" : section == Sections.Ecg && x.V(Keys.EcgLoad) == "1" ? "（負荷）" : "";
                t.Cell().Element(z => ReportService.Cell(z, true, true)).Text(section);
                t.Cell().Element(z => ReportService.Cell(z)).Column(c =>
                {
                    c.Item().Text((date == "" ? "受診日 　／　" : "受診日 " + date.Replace('-', '/')) + extra).FontSize(7.5f).FontColor(Colors.Grey.Darken2);
                    foreach (var f in fs) c.Item().Text((f.Code != "" ? f.Code + " " : "") + f.Text);
                    if (fs.Count == 0) c.Item().Text(" ");
                });
                t.Cell().Element(z => ReportService.Cell(z, false, true)).Text(JudgText(x, section) + "\n" + JudgCodeText(x, section));
            }
            t.Cell().Element(z => ReportService.Cell(z, true)).Text("次回カメラ");
            t.Cell().ColumnSpan(2).Element(z => ReportService.Cell(z)).Text($"胃 {x.V(Keys.NextGastroYears)} 年後 ／ 大腸 {x.V(Keys.NextColonYears)} 年後");
        });
        col.Item().PaddingTop(6).Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(40); });
            t.Cell().ColumnSpan(2).Element(z => ReportService.Cell(z, true, true)).Text("問診（抜粋）");
            void Q(string label, string key, string yes = "はい", string no = "いいえ") { t.Cell().Element(z => ReportService.Cell(z)).Text(label); t.Cell().Element(z => ReportService.Cell(z, false, true)).Text(x.V(key) == "1" ? yes : x.V(key) == "" ? "" : no); }
            Q("血圧を下げる薬", Keys.QBpMed); Q("インスリン注射・血糖を下げる薬", Keys.QDmMed); Q("コレステロールを下げる薬", Keys.QLipidMed); Q("喫煙", Keys.QSmoking);
            t.Cell().Element(z => ReportService.Cell(z)).Text("飲酒"); t.Cell().Element(z => ReportService.Cell(z, false, true)).Text((x.V(Keys.QAlcoholFreq) + " " + x.V(Keys.QAlcoholAmount)).Trim());
        });
    }

    string JudgText(ExamContext x, string section) => _d.Judg(x, section);
    string JudgCodeText(ExamContext x, string section) { var c = _d.JudgCode(x, section); return c == "" ? "" : "（" + c + "）"; }
}

/// <summary>結果のお知らせ（A4 4 ページ：表紙、中面 1、中面 2、裏）。プリンタの小冊子印刷で A3 二つ折りになる。</summary>
public sealed class NoticeDocument : IDocument
{
    readonly ReportData _d;
    public NoticeDocument(ReportData d) { _d = d; }
    public DocumentMetadata GetMetadata() => new() { Title = "結果のお知らせ" };

    public void Compose(IDocumentContainer container)
    {
        Cover(container);
        Inner1(container);
        Inner2(container);
        Back(container);
    }

    void Page(IDocumentContainer container, Action<PageDescriptor> extra)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(10, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontFamily(ReportService.Font).FontSize(8.5f));
            page.Footer().AlignRight().Text($"{_d.Now.Patient.FalcoId}  {_d.Now.Patient.Name}　{_d.Now.Exam.ExamDate}").FontSize(7).FontColor(Colors.Grey.Darken1);
            extra(page);
        });
    }

    void Cover(IDocumentContainer container) => Page(container, page =>
    {
        var p = _d.Now.Patient; var e = _d.Now.Exam;
        var age = p.AgeAt(DateOnly.TryParse(e.ExamDate, out var d) ? d : DateOnly.FromDateTime(DateTime.Today));
        page.Content().Column(col =>
        {
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(50); cd.RelativeColumn(2); cd.ConstantColumn(36); cd.ConstantColumn(50); cd.RelativeColumn(2); });
                void H(string s) => t.Cell().Element(c => ReportService.Cell(c, true)).Text(s);
                void D(string s, uint span = 1) { var c = t.Cell(); if (span > 1) c.ColumnSpan(span); c.Element(z => ReportService.Cell(z)).Text(s); }
                H("事業所"); D(p.Office); H("所属"); D(p.Dept, 2);
                H("カナ氏名"); D(p.Kana); H("性別"); H("生年月日"); D(ReportService.Wareki(p.BirthDate));
                H("氏名"); D(p.Name + "　様"); D(p.Sex); H("年齢"); D(age?.ToString() + " 歳");
                t.Cell().ColumnSpan(3).Element(z => z.PaddingVertical(1)); H("個人ID"); D(p.FalcoId != "" ? p.FalcoId : p.ChartNo);
            });
            col.Item().PaddingTop(120).AlignCenter().Row(r =>
            {
                r.AutoItem().Border(1).Padding(6).Column(c =>
                {
                    c.Item().Text(_d.Now.Exam.Course.Contains("ドック") ? "人間ドック" : "健康診断").FontSize(12);
                });
                r.AutoItem().PaddingLeft(12).Text("結果のお知らせ").FontSize(34).Bold();
            });
            col.Item().PaddingTop(80).AlignCenter().Width(260).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(120); cd.RelativeColumn(); });
                void R(string a, string b) { t.Cell().Element(c => ReportService.Cell(c, true)).Text(a); t.Cell().Element(c => ReportService.Cell(c)).Text(b); }
                R("今回健診実施日", Fmt(_d.Now.Exam.ExamDate)); R("前回健診実施日", Fmt(_d.Prev1?.Exam.ExamDate)); R("前々回健診実施日", Fmt(_d.Prev2?.Exam.ExamDate));
            });
            col.Item().PaddingTop(120).AlignCenter().Width(340).Border(1).Padding(10).Column(c =>
            {
                c.Item().Row(r => { r.RelativeItem().Text("健康診断実施医師名"); r.RelativeItem().Text("産業医名"); });
                c.Item().Row(r => { r.RelativeItem().Text(_d.DoctorName).FontSize(11); r.RelativeItem().Text(_d.State.Config.Clinic.OccupationalDoctor).FontSize(11); });
                c.Item().PaddingTop(6).AlignCenter().Text(_d.State.Config.Clinic.Name).FontSize(11);
            });
        });
    });

    static string Fmt(string? ymd) => string.IsNullOrEmpty(ymd) ? "" : DateOnly.TryParse(ymd, out var d) ? $"{d.Year}年{d.Month}月{d.Day}日" : ymd;

    void Head3(TableDescriptor t, string first, string second = "単位・基準値")
    {
        foreach (var s in new[] { first, second, "今回\n" + ReportData.Date(_d.Now), "前回\n" + ReportData.Date(_d.Prev1), "前々回\n" + ReportData.Date(_d.Prev2) })
            t.Cell().Element(c => ReportService.Cell(c, true, true)).Text(s).FontSize(7.5f);
    }

    void Inner1(IDocumentContainer container) => Page(container, page =>
    {
        var now = _d.Now; var p1 = _d.Prev1; var p2 = _d.Prev2; var E = _d.E;
        page.Content().Column(col =>
        {
            col.Item().Text($"個人ID：{now.Patient.FalcoId}　{now.Patient.Kana}　{now.Patient.Name}").FontSize(8);
            col.Item().PaddingTop(3).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(34); cd.ConstantColumn(64); cd.ConstantColumn(56); cd.RelativeColumn(); cd.RelativeColumn(); cd.RelativeColumn(); });
                t.Cell().Element(c => ReportService.Cell(c, true, true)).Text("");
                Head3(t, "項目");
                void Group(string name, int rows) => t.Cell().RowSpan((uint)rows).Element(c => ReportService.Cell(c, true, true)).Text(name);
                void R(string item, string unit, Func<ExamContext?, string> f)
                {
                    t.Cell().Element(c => ReportService.Cell(c)).Text(item); t.Cell().Element(c => ReportService.Cell(c)).Text(unit).FontSize(7.5f);
                    foreach (var x in new[] { now, p1, p2 }) t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(x == null ? "" : f(x));
                }
                void J(string section) => R("判定", "", x => _d.Judg(x, section));
                Group("身体計測", 7);
                R("身長", "cm", x => x!.V(Keys.Height)); R("体重", "kg", x => x!.V(Keys.Weight));
                R("標準体重", "kg", x => Num.Fmt(E.StandardWeight(x!.N(Keys.Height)))); R("肥満度", "%", x => Num.Fmt(E.Obesity(x!.N(Keys.Height), x.N(Keys.Weight))));
                R("BMI", "18.5〜24.9", x => Num.Fmt(JudgeEngine.Bmi(x!.N(Keys.Height), x.N(Keys.Weight)))); J(Sections.Measure);
                R("腹囲", (now.IsMale ? _d.C.Waist.Male : _d.C.Waist.Female).ToString("F1") + " 未満", x => x!.V(Keys.Waist));
                Group("視力", 5);
                R("右 裸眼", "", x => x!.V(Keys.VisionR)); R("左 裸眼", "", x => x!.V(Keys.VisionL)); R("右（矯正）", "", x => x!.V(Keys.VisionRc)); R("左（矯正）", "", x => x!.V(Keys.VisionLc)); J(Sections.Vision);
                Group("聴力", 6);
                R("右 1000HZ", "", x => Hear(x!.V(Keys.HearR1000))); R("右 4000HZ", "", x => Hear(x!.V(Keys.HearR4000))); R("左 1000HZ", "", x => Hear(x!.V(Keys.HearL1000))); R("左 4000HZ", "", x => Hear(x!.V(Keys.HearL4000))); R("会話法", "", x => x!.V(Keys.HearConv)); J(Sections.Hearing);
                Group("血圧", 3);
                R("1 回目", "(mmHg)", x => Bp(x!, 1)); R("2 回目", "(mmHg)", x => Bp(x!, 2)); J(Sections.Bp);
                Group("尿検査", 5);
                R("糖", "（−）", x => Q(x!.V(Keys.UrineSugar))); R("蛋白", "（−）", x => Q(x!.V(Keys.UrineProtein))); R("潜血", "（−）", x => Q(x!.V(Keys.UrineBlood))); R("ｳﾛﾋﾞﾘﾉｰｹﾞﾝ", "正常", x => x!.V(Keys.UrineUrobil)); J(Sections.Urine);
                Group("便検査", 3);
                R("潜血 1 回目", "（−）", x => Q(x!.V(Keys.Stool1))); R("潜血 2 回目", "（−）", x => Q(x!.V(Keys.Stool2))); J(Sections.Stool);
                Group("血液検査判定", _d.C.Blood.JudgedCategories.Count);
                foreach (var cat in _d.C.Blood.JudgedCategories) R(cat, "", x => _d.Judg(x, Sections.BloodPrefix + cat));
                t.Cell().ColumnSpan(3).Element(c => ReportService.Cell(c, true)).Text("食後時間");
                foreach (var x in new[] { now, p1, p2 }) t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(x == null || x.V(Keys.MealMinutes) == "" ? "" : x.V(Keys.MealMinutes) + "分");
                t.Cell().ColumnSpan(3).Element(c => ReportService.Cell(c, true)).Text("総合判定").Bold();
                foreach (var x in new[] { now, p1, p2 }) t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(_d.Judg(x, Sections.Overall)).Bold();
            });
            col.Item().PaddingTop(6).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(34); cd.RelativeColumn(); });
                t.Cell().Element(c => ReportService.Cell(c, true, true)).Text("判定\nコメント");
                var h = _d.M.Comment(now.V(Keys.CommentH))?.Text ?? ""; var c2 = _d.M.Comment(now.V(Keys.CommentC))?.Text ?? "";
                t.Cell().Element(c => ReportService.Cell(c)).MinHeight(60).Text(string.Join("\n", new[] { c2, h, now.V(Keys.FreeComment) }.Where(s => s != "")));
            });
            var met = E.JudgeMetabolic(now);
            col.Item().PaddingTop(6).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(34); cd.RelativeColumn(3); cd.ConstantColumn(34); cd.RelativeColumn(2); });
                t.Cell().ColumnSpan(2).Element(c => ReportService.Cell(c, true)).Text("メタボリックシンドローム"); t.Cell().Element(c => ReportService.Cell(c, true, true)).Text("判定"); t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(met.Label).FontSize(11);
                t.Cell().Element(c => ReportService.Cell(c, true, true)).Text("コメント"); t.Cell().ColumnSpan(3).Element(c => ReportService.Cell(c)).Text(met.Comment);
            });
        });
    });

    static string Hear(string v) => v == "なし" ? "所見なし" : v == "あり" ? "所見あり" : v;
    static string Bp(ExamContext x, int n) { var s = x.V(n == 1 ? Keys.Bp1Sys : Keys.Bp2Sys); var d = x.V(n == 1 ? Keys.Bp1Dia : Keys.Bp2Dia); return s == "" && d == "" ? "" : $"{s}/{d}"; }
    static string Q(string v) => v == "" ? "" : "（" + v + "）";

    void Inner2(IDocumentContainer container) => Page(container, page =>
    {
        var now = _d.Now;
        page.Content().Column(col =>
        {
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(2.2f); cd.RelativeColumn(1.2f); cd.ConstantColumn(44); cd.RelativeColumn(); cd.RelativeColumn(); cd.RelativeColumn(); });
                foreach (var s in new[] { "項目名", "基準値", "単位", "今回\n" + ReportData.Date(_d.Now), "前回\n" + ReportData.Date(_d.Prev1), "前々回\n" + ReportData.Date(_d.Prev2) })
                    t.Cell().Element(c => ReportService.Cell(c, true, true)).Text(s).FontSize(7.5f);
                var cats = now.Blood.Select(b => b.Category).Distinct().ToList();
                var order = _d.C.Blood.JudgedCategories.Concat(new[] { "その他", "腫瘍マーカー" }).Concat(cats).Distinct().Where(c => cats.Contains(c));
                foreach (var cat in order)
                {
                    t.Cell().ColumnSpan(6).Element(c => ReportService.Cell(c, true)).Text(cat).Bold();
                    foreach (var b in now.Blood.Where(b => b.Category == cat))
                    {
                        t.Cell().Element(c => ReportService.Cell(c)).Text("　" + b.Item);
                        t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(b.RefText.Replace("~", "〜")).FontSize(7.5f);
                        t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(b.Unit).FontSize(7.5f);
                        t.Cell().Element(c => ReportService.Cell(c, false, true)).Text((b.Flag + " " + b.Value).Trim());
                        t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(ReportData.Blood(_d.Prev1, b.Item));
                        t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(ReportData.Blood(_d.Prev2, b.Item));
                    }
                }
                if (now.Blood.Count == 0) { t.Cell().ColumnSpan(6).Element(c => ReportService.Cell(c)).Text("（血液検査なし）"); }
            });
        });
    });

    void Back(IDocumentContainer container) => Page(container, page =>
    {
        var now = _d.Now;
        page.Content().Column(col =>
        {
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(60); cd.ConstantColumn(44); cd.RelativeColumn(); cd.RelativeColumn(); cd.RelativeColumn(); });
                t.Cell().ColumnSpan(2).Element(c => ReportService.Cell(c, true, true)).Text("肺機能検査");
                foreach (var s in new[] { "今回", "前回", "前々回" }) t.Cell().Element(c => ReportService.Cell(c, true, true)).Text(s);
                void R(string item, string unit, string key) { t.Cell().Element(c => ReportService.Cell(c)).Text(item); t.Cell().Element(c => ReportService.Cell(c)).Text(unit); foreach (var x in new[] { _d.Now, _d.Prev1, _d.Prev2 }) t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(ReportData.V(x, key)); }
                R("肺活量", "ml", Keys.LungVc); R("予測肺活量", "ml", Keys.LungPredVc); R("肺活量比", "%", Keys.LungVcPct); R("努力肺活量", "ml", Keys.LungFvc); R("1 秒量", "ml", Keys.LungFev1); R("1 秒率", "%", Keys.LungFev1Pct);
                t.Cell().ColumnSpan(2).Element(c => ReportService.Cell(c, true)).Text("判定"); foreach (var x in new[] { _d.Now, _d.Prev1, _d.Prev2 }) t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(_d.Judg(x, Sections.Lung));
            });
            foreach (var section in Sections.FindingSections)
            {
                var any = new[] { _d.Now, _d.Prev1, _d.Prev2 }.Any(x => x != null && (x.Findings.Any(f => f.Section == section) || x.Existing.Any(j => j.Section == section)));
                if (!any && !(_d.M.CourseOf(now.Exam.Course)?.Sections.Contains(section) ?? false)) continue;
                col.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(60); cd.RelativeColumn(); cd.RelativeColumn(); cd.RelativeColumn(); });
                    t.Cell().Element(c => ReportService.Cell(c, true, true)).Text(section).Bold();
                    foreach (var x in new[] { _d.Now, _d.Prev1, _d.Prev2 })
                    {
                        var date = x == null ? "" : (x.V(Keys.SectionDate(section)) is { Length: > 0 } sd ? sd : x.Exam.ExamDate).Replace('-', '.');
                        var extra = x != null && section == Sections.ChestXray ? (x.V(Keys.XrayDirect) == "" ? "直接" : x.V(Keys.XrayDirect)) : "";
                        t.Cell().Element(c => ReportService.Cell(c, true, true)).Text(x == null || !x.Findings.Any(f => f.Section == section) && !x.Existing.Any(j => j.Section == section) ? "" : $"{extra} {date}".Trim()).FontSize(7.5f);
                    }
                    t.Cell().Element(c => ReportService.Cell(c, true, true)).Text("所見");
                    foreach (var x in new[] { _d.Now, _d.Prev1, _d.Prev2 }) t.Cell().Element(c => ReportService.Cell(c)).MinHeight(30).Text(_d.Findings(x, section));
                    t.Cell().Element(c => ReportService.Cell(c, true, true)).Text("判定");
                    foreach (var x in new[] { _d.Now, _d.Prev1, _d.Prev2 }) t.Cell().Element(c => ReportService.Cell(c, false, true)).Text(_d.Judg(x, section));
                });
            }
        });
    });
}

/// <summary>検査内容の説明（A4 2 ページ）＋ 次回カメラの年数。</summary>
public sealed class ExplanationDocument : IDocument
{
    readonly ReportData _d;
    public ExplanationDocument(ReportData d) { _d = d; }
    public DocumentMetadata GetMetadata() => new() { Title = "検査内容の説明" };

    public void Compose(IDocumentContainer container)
    {
        var rows = ExplanationText.Rows;
        var split = Array.FindIndex(rows, r => r.Item == "CRP 定量");
        container.Page(page => Compose(page, rows.Take(split).ToArray(), false));
        container.Page(page => Compose(page, rows.Skip(split).ToArray(), true));
    }

    void Compose(PageDescriptor page, ExplanationText.Row[] rows, bool last)
    {
        page.Size(PageSizes.A4);
        page.Margin(12, Unit.Millimetre);
        page.DefaultTextStyle(t => t.FontFamily(ReportService.Font).FontSize(8.5f));
        page.Content().Column(col =>
        {
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(40); cd.ConstantColumn(90); cd.RelativeColumn(); });
                t.Cell().ColumnSpan(2).Element(c => c.Background("#5a8f3a").Padding(3).AlignCenter()).Text("検査項目").FontColor(Colors.White);
                t.Cell().Element(c => c.Background("#5a8f3a").Padding(3).AlignCenter()).Text("検査内容の説明").FontColor(Colors.White);
                string? lastGroup = null; var i = 0;
                var groups = rows.GroupBy(r => r.Group).ToList();
                foreach (var r in rows)
                {
                    var bg = i++ % 2 == 0 ? "#eef5e6" : "#ffffff";
                    if (r.Group != lastGroup)
                    {
                        var n = rows.Count(x => x.Group == r.Group);
                        if (r.Group == "") t.Cell().ColumnSpan(2).Element(c => c.Background("#8bbf5f").Padding(3).AlignMiddle().AlignCenter()).Text(r.Item);
                        else { t.Cell().RowSpan((uint)n).Element(c => c.Background("#8bbf5f").Padding(3).AlignMiddle().AlignCenter()).Text(r.Group).FontColor(Colors.White); t.Cell().Element(c => c.Background(bg).Padding(3).AlignMiddle()).Text(r.Item); }
                        lastGroup = r.Group;
                    }
                    else if (r.Group == "") { lastGroup = null; t.Cell().ColumnSpan(2).Element(c => c.Background("#8bbf5f").Padding(3).AlignMiddle().AlignCenter()).Text(r.Item); }
                    else t.Cell().Element(c => c.Background(bg).Padding(3).AlignMiddle()).Text(r.Item);
                    t.Cell().Element(c => c.Background(bg).Padding(3).AlignMiddle()).Text(r.Text);
                }
            });
            if (last)
            {
                var g = _d.Now.V(Keys.NextGastroYears); var cl = _d.Now.V(Keys.NextColonYears);
                col.Item().PaddingTop(14).Text(t => { t.Span(g == "" ? "□" : "☑").FontSize(12); t.Span($" 次回、胃カメラは（ {g,4} ）年後に受けましょう。").FontSize(11); });
                col.Item().PaddingTop(4).Text(t => { t.Span(cl == "" ? "□" : "☑").FontSize(12); t.Span($" 次回、大腸カメラは（ {cl,4} ）年後に受けましょう。").FontSize(11); });
            }
        });
        page.Footer().AlignRight().Text($"{_d.Now.Patient.FalcoId}  {_d.Now.Patient.Name}　{_d.Now.Exam.ExamDate}").FontSize(7).FontColor(Colors.Grey.Darken1);
    }
}
