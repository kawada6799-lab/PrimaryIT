using Kenshin.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Kenshin.Web;

public static class Api
{
    public sealed record UnlockReq(string Password);
    public sealed record PinReq(string Pin);
    public sealed record PatientReq(long? Id, string Name, string? Kana, string? Sex, string? BirthDate, string? FalcoId, string? ChartNo, string? EmployeeNo, string? Office, string? Dept);
    public sealed record ExamReq(long PatientId, string ExamDate, string? Course);
    public sealed record ExamUpdateReq(string? ExamDate, string? Course, string? Status);
    public sealed record JudgmentReq(string Section, string? Code);
    public sealed record FindingReq(string Section, string? Code, string? Text);
    public sealed record BloodReq(string Item, string Value, string? Unit, string? RefText, string? Flag);

    public static void Map(WebApplication app, AppState state, ExamService svc)
    {
        var m = state.Masters;

        // ---- 状態・認証 ----
        app.MapGet("/api/status", () =>
        {
            var g = state.Guard.Check();
            return Results.Json(new
            {
                unlocked = state.Unlocked, blocked = g.Blocked, reason = g.Reason, guardEnabled = state.Guard.Enabled,
                voice = new { available = state.Voice.Available, status = state.Voice.Status },
                clinic = state.Config.Clinic, lan = state.Config.Listen.LanAddress, version = typeof(Api).Assembly.GetName().Version?.ToString(),
            });
        });
        app.MapPost("/api/unlock", (UnlockReq r) => state.Unlock(r.Password ?? "") ? Results.Ok(new { ok = true }) : Results.Json(new { error = "パスワードが違います（または DB が壊れています）" }, statusCode: 401));
        app.MapPost("/api/pin", (PinReq r, HttpContext ctx) =>
        {
            if (string.IsNullOrEmpty(state.Config.Pin) || r.Pin == state.Config.Pin)
            {
                ctx.Response.Cookies.Append(Host.PinCookie, Host.PinToken(state, state.Config.Pin), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.Now.AddDays(30) });
                return Results.Ok(new { ok = true });
            }
            return Results.Json(new { error = "PIN が違います" }, statusCode: 401);
        });
        app.MapGet("/ca.crt", () =>
        {
            var p = Path.Combine(state.DataDir, "ca.crt");
            return File.Exists(p) ? Results.File(p, "application/x-x509-ca-cert", "kenshin-ca.crt") : Results.NotFound();
        });

        // ---- マスター ----
        app.MapGet("/api/masters", () => Results.Json(new
        {
            categories = m.Categories, comments = m.Comments, findings = m.Findings, courses = m.Courses, bloodItems = m.BloodItems,
            sections = Sections.Order, findingSections = Sections.FindingSections, statuses = ExamStatus.All,
            sectionLabels = state.Criteria.SectionLabels, bloodCategories = state.Criteria.Blood.JudgedCategories,
            criteria = new { state.Criteria.Bmi, state.Criteria.Waist, state.Criteria.BloodPressure, state.Criteria.Urine, state.Criteria.Vision, state.Criteria.Hearing, state.Criteria.Stool, state.Criteria.LungFunction, metabolic = state.Criteria.Metabolic },
        }));

        // ---- 受診者 ----
        app.MapGet("/api/patients", (string? q) => Results.Json(state.RequireDb().SearchPatients(q ?? "")));
        app.MapGet("/api/patients/{id:long}", (long id) => state.RequireDb().GetPatient(id) is { } p ? Results.Json(p) : Results.NotFound());
        app.MapPost("/api/patients", (PatientReq r) =>
        {
            if (string.IsNullOrWhiteSpace(r.Name)) return Results.BadRequest(new { error = "氏名は必須です" });
            var db = state.RequireDb();
            var p = r.Id is > 0 ? db.GetPatient(r.Id.Value) ?? new Patient() : new Patient();
            p.Name = r.Name.Trim(); p.Kana = (r.Kana ?? "").Trim(); p.Sex = r.Sex ?? ""; p.BirthDate = NormalizeDate(r.BirthDate);
            p.FalcoId = (r.FalcoId ?? "").Trim(); p.ChartNo = (r.ChartNo ?? "").Trim(); p.EmployeeNo = (r.EmployeeNo ?? "").Trim(); p.Office = (r.Office ?? "").Trim(); p.Dept = (r.Dept ?? "").Trim();
            db.SavePatient(p);
            return Results.Json(p);
        });
        app.MapGet("/api/patients/{id:long}/exams", (long id) => Results.Json(state.RequireDb().ExamsOfPatient(id).Select(svc.ExamSummary)));

        // ---- 健診回 ----
        app.MapGet("/api/exams", (string? date) =>
        {
            var d = string.IsNullOrEmpty(date) ? DateTime.Today.ToString("yyyy-MM-dd") : date;
            return Results.Json(state.RequireDb().ExamsOn(d).Select(svc.ExamSummary));
        });
        app.MapGet("/api/exams/pending", (int? days) => Results.Json(state.RequireDb().PendingExams(days ?? 90).Select(svc.ExamSummary)));
        app.MapGet("/api/exams/followup", (int? days) =>
        {
            // 要受診・要再検査・要精密検査・要治療の人（第 4 段階の追跡一覧の土台）
            var db = state.RequireDb();
            var since = DateTime.Today.AddDays(-(days ?? 365)).ToString("yyyy-MM-dd");
            var list = new List<object>();
            foreach (var e in db.ExamsBetween(since, "9999-12-31"))
            {
                var js = db.GetJudgments(e.Id);
                var heavy = js.Where(j => j.Section != Sections.Overall && m.Rank(j.Code) >= m.Rank("190")).ToList();
                if (heavy.Count == 0) continue;
                list.Add(new { exam = svc.ExamSummary(e), items = heavy.Select(j => new { j.Section, j.Code, name = m.CategoryName(j.Code) }) });
            }
            return Results.Json(list);
        });
        app.MapPost("/api/exams", (ExamReq r) =>
        {
            var db = state.RequireDb();
            if (db.GetPatient(r.PatientId) == null) return Results.BadRequest(new { error = "受診者がいません" });
            var date = NormalizeDate(r.ExamDate);
            if (date == "") return Results.BadRequest(new { error = "健診実施日が不正です" });
            var existing = db.ExamsOfPatient(r.PatientId).FirstOrDefault(e => e.ExamDate == date);
            if (existing != null) return Results.Json(svc.ExamSummary(existing));
            var e = new Exam { PatientId = r.PatientId, ExamDate = date, Course = r.Course ?? m.Courses.FirstOrDefault()?.Code ?? "" };
            db.SaveExam(e);
            db.Log("健診回を作成", e.Id, date);
            svc.FillDefaults(e.Id); // 既定値＝正常（異常のときだけ触る）
            svc.Recompute(e.Id);
            return Results.Json(svc.ExamSummary(e));
        });
        app.MapGet("/api/exams/{id:long}", (long id) => svc.Dto(id) is { } d ? Results.Json(d) : Results.NotFound());
        app.MapPut("/api/exams/{id:long}", (long id, ExamUpdateReq r) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (!string.IsNullOrEmpty(r.ExamDate)) e.ExamDate = NormalizeDate(r.ExamDate);
            if (r.Course != null) e.Course = r.Course;
            if (r.Status != null && ExamStatus.All.Contains(r.Status)) { e.Status = r.Status; if (r.Status == ExamStatus.Mailed) db.Log("郵送済", id, ""); }
            db.SaveExam(e);
            svc.Recompute(id);
            return Results.Json(svc.Dto(id));
        });
        app.MapDelete("/api/exams/{id:long}", (long id) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (e.Status is ExamStatus.Confirmed or ExamStatus.Mailed) return Results.BadRequest(new { error = "確定済みの健診回は削除できません。先に確定を解除してください" });
            foreach (var t in new[] { "exam_values", "judgments", "findings", "blood_results", "issued_pdfs" }) db.Exec($"DELETE FROM {t} WHERE exam_id=@e", ("@e", id));
            db.Exec("DELETE FROM exams WHERE id=@e", ("@e", id));
            db.Log("健診回を削除", id, "");
            return Results.Ok(new { ok = true });
        });

        // ---- 入力値（項目ごとに即時保存 → 再判定）----
        app.MapPut("/api/exams/{id:long}/values", (long id, Dictionary<string, string> values) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (e.Status == ExamStatus.Confirmed || e.Status == ExamStatus.Mailed) return Results.BadRequest(new { error = "確定済みです。変更するには確定を解除してください" });
            foreach (var (k, v) in values) db.SetValue(id, k, (v ?? "").Trim());
            svc.Recompute(id);
            return Results.Json(svc.Dto(id));
        });

        // ---- 判定（手動）----
        app.MapPut("/api/exams/{id:long}/judgment", (long id, JudgmentReq r) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (e.Status == ExamStatus.Confirmed || e.Status == ExamStatus.Mailed) return Results.BadRequest(new { error = "確定済みです" });
            if (string.IsNullOrEmpty(r.Code)) db.SetJudgment(id, r.Section, null, false); // 自動に戻す
            else { if (m.Category(r.Code) == null) return Results.BadRequest(new { error = "判定区分が不正です" }); db.SetJudgment(id, r.Section, r.Code, true); }
            db.Log("判定を変更", id, $"{r.Section} → {(r.Code ?? "自動")}");
            svc.Recompute(id);
            return Results.Json(svc.Dto(id));
        });

        // ---- 所見 ----
        app.MapPost("/api/exams/{id:long}/findings", (long id, FindingReq r) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (e.Status == ExamStatus.Confirmed || e.Status == ExamStatus.Mailed) return Results.BadRequest(new { error = "確定済みです" });
            var text = r.Text ?? "";
            if (!string.IsNullOrEmpty(r.Code)) { var fm = m.Finding(r.Section, r.Code); if (fm == null) return Results.BadRequest(new { error = "所見コードが不正です" }); text = fm.Text; }
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "所見が空です" });
            var sort = db.GetFindings(id).Count(f => f.Section == r.Section);
            db.AddFinding(new Finding { ExamId = id, Section = r.Section, Code = r.Code ?? "", Text = text, Sort = sort });
            if (string.IsNullOrEmpty(db.GetValues(id).GetValueOrDefault(Keys.SectionDate(r.Section)))) db.SetValue(id, Keys.SectionDate(r.Section), e.ExamDate);
            svc.Recompute(id);
            return Results.Json(svc.Dto(id));
        });
        app.MapDelete("/api/exams/{id:long}/findings/{fid:long}", (long id, long fid) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (e.Status == ExamStatus.Confirmed || e.Status == ExamStatus.Mailed) return Results.BadRequest(new { error = "確定済みです" });
            db.DeleteFinding(id, fid);
            svc.Recompute(id);
            return Results.Json(svc.Dto(id));
        });

        // ---- 血液（手入力・修正）----
        app.MapPut("/api/exams/{id:long}/blood", (long id, BloodReq r) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (e.Status == ExamStatus.Confirmed || e.Status == ExamStatus.Mailed) return Results.BadRequest(new { error = "確定済みです" });
            if (string.IsNullOrWhiteSpace(r.Item)) return Results.BadRequest(new { error = "項目名が空です" });
            var (lo, hi) = Lab.FalcoPdfReader.ParseRange(r.RefText ?? "");
            var b = new BloodResult { Item = r.Item.Trim(), Value = r.Value.Trim(), Unit = r.Unit ?? "", RefText = r.RefText ?? "", RefLow = lo, RefHigh = hi, Flag = r.Flag ?? "", TakenAt = e.ExamDate };
            b.Category = state.Criteria.Blood.CategoryOf(b.Item);
            state.Masters.Canonicalize(b, db.GetPatient(e.PatientId)?.Sex);
            if (b.Value == "") { foreach (var ex in db.GetBlood(id).Where(x => x.Item == b.Item)) db.DeleteBloodItem(id, ex.Id); svc.Recompute(id); return Results.Json(svc.Dto(id)); }
            if (b.Flag == "" && b.NumericValue is { } v) { if (b.RefHigh != null && v > b.RefHigh) b.Flag = "↑"; else if (b.RefLow != null && v < b.RefLow) b.Flag = "↓"; }
            db.UpsertBloodItem(id, b);
            svc.Recompute(id);
            return Results.Json(svc.Dto(id));
        });
        app.MapDelete("/api/exams/{id:long}/blood/{bid:long}", (long id, long bid) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (e.Status == ExamStatus.Confirmed || e.Status == ExamStatus.Mailed) return Results.BadRequest(new { error = "確定済みです" });
            db.DeleteBloodItem(id, bid);
            svc.Recompute(id);
            return Results.Json(svc.Dto(id));
        });

        // ---- 確定 ----
        app.MapPost("/api/exams/{id:long}/confirm", (long id) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            svc.Recompute(id);
            var x = svc.Context(id)!;
            var js = db.GetJudgments(id);
            if (!js.Any(j => j.Section == Sections.Overall)) return Results.BadRequest(new { error = "判定がありません" });
            var vals = db.GetValues(id);
            var (h, c, _) = state.Engine.SuggestComments(x, js.First(j => j.Section == Sections.Overall).Code);
            if (string.IsNullOrEmpty(vals.GetValueOrDefault(Keys.CommentH))) db.SetValue(id, Keys.CommentH, h);
            if (string.IsNullOrEmpty(vals.GetValueOrDefault(Keys.CommentC))) db.SetValue(id, Keys.CommentC, c);
            e = db.GetExam(id)!;
            e.Status = ExamStatus.Confirmed; e.ConfirmedAt = Data.Db.Now(); db.SaveExam(e);
            db.Log("確定", id, "");
            string? pdf = null;
            try { pdf = Reports.ReportService.SaveNotice(state, svc, id); } catch (Exception ex) { db.Log("結果のお知らせ PDF の保存に失敗", id, ex.Message); }
            return Results.Json(new { ok = true, pdf, dto = svc.Dto(id) });
        });
        app.MapPost("/api/exams/{id:long}/unconfirm", (long id) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            e.Status = ExamStatus.Ready; e.ConfirmedAt = null; db.SaveExam(e);
            db.Log("確定を解除", id, "");
            svc.Recompute(id);
            return Results.Json(svc.Dto(id));
        });

        app.MapPost("/api/exams/{id:long}/defaults", (long id) =>
        {
            var db = state.RequireDb(); var e = db.GetExam(id); if (e == null) return Results.NotFound();
            if (e.Status == ExamStatus.Confirmed || e.Status == ExamStatus.Mailed) return Results.BadRequest(new { error = "確定済みです" });
            var n = svc.FillDefaults(id); svc.Recompute(id);
            return Results.Json(new { count = n, dto = svc.Dto(id) });
        });

        // ---- 記録 ----
        app.MapGet("/api/events", (int? limit) => Results.Json(state.RequireDb().RecentEvents(limit ?? 200)));

        Lab.LabEndpoints.Map(app, state, svc);
        Reports.ReportService.Map(app, state, svc);
        Voice.VoiceEndpoints.Map(app, state);
    }

    public static string NormalizeDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Num.ToHalfWidth(s.Trim()).Replace('／', '/').Replace('．', '.').Replace('/', '-').Replace('.', '-').Replace('年', '-').Replace('月', '-').Replace("日", "");
        if (DateOnly.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyyMMdd" }, out var d)) return d.ToString("yyyy-MM-dd");
        // 和暦（S52-10-7 / H2-9-24 / R5-1-1）
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^([MTSHR]|明治|大正|昭和|平成|令和)\s*(\d{1,2})-(\d{1,2})-(\d{1,2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var era = m.Groups[1].Value.ToUpperInvariant();
            var baseYear = era switch { "M" or "明治" => 1867, "T" or "大正" => 1911, "S" or "昭和" => 1925, "H" or "平成" => 1988, _ => 2018 };
            var y = baseYear + int.Parse(m.Groups[2].Value);
            if (DateOnly.TryParse($"{y}-{m.Groups[3].Value}-{m.Groups[4].Value}", out var wd)) return wd.ToString("yyyy-MM-dd");
        }
        return DateOnly.TryParse(s, out var d2) ? d2.ToString("yyyy-MM-dd") : "";
    }
}
