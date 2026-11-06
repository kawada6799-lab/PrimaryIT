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
            if (ext is not (".pdf" or ".csv" or ".txt" or ".tsv")) continue;
            var c = new ImportCandidate { FileName = Path.GetFileName(f), Size = new FileInfo(f).Length };
            try
            {
                var rep = FalcoPdfReader.ReadFile(f);
                c.TakenAt = rep.TakenAt; c.PatientHint = rep.PatientHint; c.IdHint = rep.IdHint; c.NameHint = rep.NameHint; c.ItemCount = rep.Items.Count; c.Error = rep.Error;
                Suggest(c);
            }
            catch (Exception ex) { c.Error = ex.Message; }
            var lf = Db.GetLabFile(c.FileName);
            if (lf != null) c.Imported = lf.Status + (lf.ExamId != null ? $"（健診回 {lf.ExamId}）" : "");
            list.Add(c);
        }
        return list;
    }

    void Suggest(ImportCandidate c)
    {
        var db = Db;
        var pats = db.FindPatientsByIds(c.IdHint, c.IdHint);
        if (pats.Count == 0 && c.NameHint != "") pats = db.FindPatientsByNameBirth(c.NameHint, "");
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
    }
}
