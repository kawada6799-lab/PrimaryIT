using Kenshin.Data;
using Kenshin.Domain;
using Kenshin.Web;

namespace Kenshin.Lab;

public sealed class ImportCandidate
{
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string TakenAt { get; set; } = "";
    public string PatientHint { get; set; } = "";
    public string IdHint { get; set; } = "";
    public string NameHint { get; set; } = "";
    public int ItemCount { get; set; }
    public string Error { get; set; } = "";
    public long? SuggestedExamId { get; set; }
    public string SuggestedLabel { get; set; } = "";
    public string Match { get; set; } = "";
    public string Imported { get; set; } = "";
    /// <summary>pdf／text／photo</summary>
    public string Kind { get; set; } = "pdf";
    /// <summary>写真：読み取り済みか（.ocr.json があるか）</summary>
    public bool Read { get; set; }
    public int OkCount { get; set; }
    public int CheckCount { get; set; }
    public string Page { get; set; } = "";
}

/// <summary>取込フォルダの PDF を読み、受診者と突き合わせて血液検査結果に入れる。</summary>
public sealed class LabImporter
{
    readonly AppState _s;
    readonly ExamService _svc;
    public LabImporter(AppState s, ExamService svc) { _s = s; _svc = svc; }
    Db Db => _s.RequireDb();

    public List<ImportCandidate> Scan()
    {
        var dir = _s.ImportDir;
        Directory.CreateDirectory(dir);
        var list = new List<ImportCandidate>();
        foreach (var f in Directory.GetFiles(dir).OrderBy(f => f))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".pdf" or ".csv" or ".txt" or ".tsv") && !PhotoImporter.IsImage(f)) continue;
            var c = new ImportCandidate { FileName = Path.GetFileName(f), Size = new FileInfo(f).Length };
            try
            {
                if (PhotoImporter.IsImage(f))
                {
                    c.Kind = "photo";
                    var side = PhotoImporter.SidecarPath(f);
                    if (File.Exists(side))
                    {
                        var r = System.Text.Json.JsonSerializer.Deserialize<PhotoReadResult>(File.ReadAllText(side), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (r != null)
                        {
                            c.Read = true; c.TakenAt = r.TakenAt; c.IdHint = r.PatientNo; c.NameHint = r.NameKana; c.ItemCount = r.Items.Count; c.OkCount = r.OkCount; c.CheckCount = r.CheckCount; c.Page = r.Page; c.Error = r.Error;
                            c.PatientHint = string.Join(" / ", new[] { r.NameKana, r.PatientNo != "" ? "番号 " + r.PatientNo : "", r.RequestNo != "" ? r.RequestNo + "(" + r.Page + ")" : "" }.Where(x => x != ""));
                        }
                    }
                    if (c.Read) Suggest(c); else c.Match = "未読取";
                }
                else
                {
                    c.Kind = ext == ".pdf" ? "pdf" : "text";
                    var rep = FalcoPdfReader.ReadFile(f);
                    c.TakenAt = rep.TakenAt; c.PatientHint = rep.PatientHint; c.IdHint = rep.IdHint; c.NameHint = rep.NameHint; c.ItemCount = rep.Items.Count; c.Error = rep.Error;
                    Suggest(c);
                }
            }
            catch (Exception ex) { c.Error = ex.Message; }
            var lf = Db.GetLabFile(c.FileName);
            if (lf != null) c.Imported = lf.Status + (lf.ExamId != null ? $"（健診回 {lf.ExamId}）" : "");
            list.Add(c);
        }
        return list;
    }

    public void SuggestFor(ImportCandidate c) => Suggest(c);
    void Suggest(ImportCandidate c)
    {
        var db = Db;
        var pats = db.FindPatientsByIds(c.IdHint, c.IdHint);
        if (pats.Count == 0 && c.NameHint != "") pats = db.FindPatientsByNameBirth(c.NameHint, "");
        if (pats.Count == 0 && c.NameHint != "") pats = db.FindPatientsByKana(c.NameHint);
        List<Exam> exams;
        if (pats.Count == 1)
        {
            exams = db.ExamsOfPatient(pats[0].Id);
            var same = exams.Where(e => e.ExamDate == c.TakenAt).ToList();
            var pick = same.Count == 1 ? same[0] : exams.FirstOrDefault(e => e.Status != ExamStatus.Mailed && e.Status != ExamStatus.Confirmed) ?? exams.FirstOrDefault();
            if (pick != null) { c.SuggestedExamId = pick.Id; c.Match = same.Count == 1 ? "一致" : "要確認"; c.SuggestedLabel = $"{pats[0].Name} ／ {pick.ExamDate}"; return; }
        }
        if (c.TakenAt != "")
        {
            exams = db.ExamsOn(c.TakenAt);
            if (exams.Count == 1) { var p = db.GetPatient(exams[0].PatientId); c.SuggestedExamId = exams[0].Id; c.Match = "要確認"; c.SuggestedLabel = $"{p?.Name} ／ {exams[0].ExamDate}（同日唯一）"; return; }
        }
        c.Match = "手で選ぶ";
    }

    public ParsedLabReport Preview(string fileName) => FalcoPdfReader.ReadFile(SafePath(fileName));

    /// <summary>取り込み：血液結果を置き換え、ファイルを「済」フォルダへ移す。</summary>
    public (bool ok, string message, int count) Apply(string fileName, long examId)
    {
        var path = SafePath(fileName);
        var db = Db;
        var exam = db.GetExam(examId); if (exam == null) return (false, "健診回がありません", 0);
        if (exam.Status is ExamStatus.Confirmed or ExamStatus.Mailed) return (false, "確定済みの健診回には取り込めません", 0);
        var rep = FalcoPdfReader.ReadFile(path);
        if (rep.Items.Count == 0) return (false, rep.Error != "" ? rep.Error : "項目を読み取れませんでした", 0);
        var sex = db.GetPatient(exam.PatientId)?.Sex;
        foreach (var it in rep.Items) { it.ExamId = examId; it.SourceFile = rep.FileName; it.TakenAt = rep.TakenAt; it.Category = _s.Criteria.Blood.CategoryOf(it.Item); _s.Masters.Canonicalize(it, sex); }
        // 同じ項目が 2 行読めたとき（HBs 抗原の定性と定量など）は先に読めた方を残す
        var items = rep.Items.GroupBy(i => i.Item).Select(g => g.First()).ToList();
        db.ReplaceBlood(examId, items);
        db.SaveLabFile(new LabFile { FileName = rep.FileName, TakenAt = rep.TakenAt, PatientHint = rep.PatientHint, ExamId = examId, Status = "取込済", ImportedAt = Db.Now(), ItemCount = rep.Items.Count });
        db.Log("採血結果を取り込み", examId, $"{rep.FileName}（{rep.Items.Count} 項目）");
        _svc.Recompute(examId);
        try
        {
            var done = Path.Combine(_s.ImportDir, "済"); Directory.CreateDirectory(done);
            var dest = Path.Combine(done, rep.FileName);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(path, dest);
        }
        catch (Exception ex) { return (true, "取り込みましたが、ファイルを「済」へ移せませんでした: " + ex.Message, rep.Items.Count); }
        return (true, "取り込みました", rep.Items.Count);
    }

    string SafePath(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var path = Path.Combine(_s.ImportDir, name);
        if (!File.Exists(path)) throw new FileNotFoundException("ファイルがありません: " + name);
        return path;
    }
}

public static class LabEndpoints
{
    public sealed record ApplyReq(string File, long ExamId);
    public sealed record ApplyPhotoReq(string File, long ExamId, List<PhotoImporter.ConfirmedItem>? Items, string? TakenAt);
    public static void Map(WebApplication app, AppState state, ExamService svc)
    {
        var importer = new LabImporter(state, svc);
        app.MapGet("/api/import/files", () => Results.Json(new { dir = state.ImportDir, files = importer.Scan() }));
        app.MapGet("/api/import/preview", (string file) =>
        {
            var rep = importer.Preview(file);
            return Results.Json(new { rep.FileName, rep.TakenAt, rep.ReceivedAt, rep.ReportedAt, rep.RequestNo, rep.PatientHint, rep.IdHint, rep.NameHint, rep.Error, items = rep.Items, lines = rep.Lines.Take(80) });
        });
        app.MapPost("/api/import/apply", (ApplyReq r) =>
        {
            var (ok, msg, n) = importer.Apply(r.File, r.ExamId);
            return ok ? Results.Json(new { ok, message = msg, count = n, dto = svc.Dto(r.ExamId) }) : Results.BadRequest(new { error = msg });
        });

        // ---- 写真（携帯・タブレット・PC から）----
        var photos = new PhotoImporter(state, svc);
        app.MapPost("/api/import/upload", async (HttpRequest req) =>
        {
            state.RequireDb();
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "写真がありません" });
            var form = await req.ReadFormAsync();
            var saved = new List<object>();
            foreach (var f in form.Files)
            {
                if (f.Length == 0) continue;
                string name;
                try { using var st = f.OpenReadStream(); name = photos.Save(st, f.FileName); }
                catch (Exception ex) { saved.Add(new { fileName = f.FileName, error = "画像として読めません: " + ex.Message }); continue; }
                PhotoReadResult r;
                try { r = photos.Read(name); } catch (Exception ex) { r = new PhotoReadResult { FileName = name, Error = ex.Message }; }
                saved.Add(new { fileName = name, r.Error, items = r.Items.Count, ok = r.OkCount, check = r.CheckCount, r.NameKana, r.PatientNo, r.TakenAt, r.Page });
            }
            if (saved.Count == 0) return Results.BadRequest(new { error = "写真がありません" });
            return Results.Json(new { files = saved });
        });
        app.MapGet("/api/import/photo", (string file, bool? reread) =>
        {
            state.RequireDb();
            var r = photos.Read(file, reread == true);
            var crops = r.Error == "" ? photos.Crops(file, r) : new Dictionary<string, string>();
            // 受診者との突合の候補
            var c = new ImportCandidate { FileName = file, TakenAt = r.TakenAt, IdHint = r.PatientNo, NameHint = r.NameKana };
            importer.SuggestFor(c);
            return Results.Json(new { result = r, crops, suggestedExamId = c.SuggestedExamId, suggestedLabel = c.SuggestedLabel, match = c.Match });
        });
        app.MapGet("/api/import/image", (string file) => { state.RequireDb(); return Results.File(photos.Preview(file), "image/jpeg"); });
        app.MapPost("/api/import/apply-photo", (ApplyPhotoReq r) =>
        {
            var (ok, msg, n) = photos.Apply(r.File, r.ExamId, r.Items ?? new(), r.TakenAt ?? "");
            return ok ? Results.Json(new { ok, message = msg, count = n, dto = svc.Dto(r.ExamId) }) : Results.BadRequest(new { error = msg });
        });
    }
}
