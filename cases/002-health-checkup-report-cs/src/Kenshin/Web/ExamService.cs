using Kenshin.Config;
using Kenshin.Data;
using Kenshin.Domain;
using Kenshin.Judge;

namespace Kenshin.Web;

/// <summary>健診回の読み出し・再判定・画面用の形への変換。</summary>
public sealed class ExamService
{
    readonly AppState _s;
    public ExamService(AppState s) { _s = s; }
    Db Db => _s.RequireDb();
    Masters M => _s.Masters;

    public ExamContext? Context(long examId)
    {
        var exam = Db.GetExam(examId); if (exam == null) return null;
        var patient = Db.GetPatient(exam.PatientId) ?? new Patient { Id = exam.PatientId, Name = "（不明）" };
        var blood = Db.GetBlood(examId);
        foreach (var b in blood) b.Category = _s.Criteria.Blood.CategoryOf(b.Item); // 分野は常に今の設定で決める（設定を直したら過去の回にも効く）
        return new ExamContext
        {
            Patient = patient, Exam = exam, Values = Db.GetValues(examId), Findings = Db.GetFindings(examId), Blood = blood, Existing = Db.GetJudgments(examId),
        };
    }

    /// <summary>自動判定をやり直して保存し、状態も進める。</summary>
    public List<Judgment> Recompute(long examId)
    {
        var x = Context(examId); if (x == null) return new();
        var js = _s.Engine.ComputeAll(x, Db.Now());
        Db.ReplaceJudgments(examId, js);
        AdvanceStatus(x, js);
        return js;
    }

    void AdvanceStatus(ExamContext x, List<Judgment> js)
    {
        var e = x.Exam;
        if (e.Status is ExamStatus.Confirmed or ExamStatus.Mailed) return;
        var course = M.CourseOf(e.Course);
        var required = course?.Required ?? new List<string>();
        var done = js.Select(j => j.Section).ToHashSet();
        var hasBlood = js.Any(j => j.Section.StartsWith(Sections.BloodPrefix));
        var findingMissing = required.Where(s => Sections.FindingSections.Contains(s) && !done.Contains(s)).Any();
        var measureMissing = required.Where(s => !Sections.FindingSections.Contains(s) && s != "血液検査" && !done.Contains(s)).Any();
        string status;
        if (measureMissing) status = ExamStatus.Entering;
        else if (findingMissing) status = ExamStatus.WaitingFindings;
        else if (required.Contains("血液検査") && !hasBlood) status = ExamStatus.WaitingBlood;
        else status = ExamStatus.Ready;
        if (status != e.Status) { e.Status = status; Db.SaveExam(e); }
    }

    public object? Dto(long examId)
    {
        var x = Context(examId); if (x == null) return null;
        var js = x.Existing;
        var overall = js.FirstOrDefault(j => j.Section == Sections.Overall)?.Code;
        var (h, c, cCands) = _s.Engine.SuggestComments(x, overall);
        var metabolic = _s.Engine.JudgeMetabolic(x);
        var missing = _s.Engine.MissingRequired(x, js);
        var age = x.Patient.AgeAt(DateOnly.TryParse(x.Exam.ExamDate, out var d) ? d : DateOnly.FromDateTime(DateTime.Today));
        var history = Db.ExamsOfPatient(x.Patient.Id).Where(e => e.Id != examId && string.CompareOrdinal(e.ExamDate, x.Exam.ExamDate) < 0).Take(2)
            .Select(e => new { e.Id, e.ExamDate, e.Status, e.Course }).ToList();
        var bmi = JudgeEngine.Bmi(x.N(Keys.Height), x.N(Keys.Weight));
        var reasons = new Dictionary<string, string>();
        foreach (var r in new[] { _s.Engine.JudgeMeasure(x), _s.Engine.JudgeVision(x), _s.Engine.JudgeHearing(x), _s.Engine.JudgeBp(x), _s.Engine.JudgeUrine(x), _s.Engine.JudgeStool(x), _s.Engine.JudgeLung(x) }) reasons[r.Section] = r.Reason;
        foreach (var r in _s.Engine.JudgeBlood(x)) reasons[r.Section] = r.Reason;
        return new
        {
            exam = x.Exam,
            patient = new { x.Patient.Id, x.Patient.FalcoId, x.Patient.ChartNo, x.Patient.EmployeeNo, x.Patient.Name, x.Patient.Kana, x.Patient.Sex, x.Patient.BirthDate, x.Patient.Office, x.Patient.Dept, age },
            values = x.Values,
            computed = new { bmi, standardWeight = _s.Engine.StandardWeight(x.N(Keys.Height)), obesity = _s.Engine.Obesity(x.N(Keys.Height), x.N(Keys.Weight)) },
            judgments = js.Select(j => new { j.Section, j.Code, name = M.CategoryName(j.Code), label = _s.Criteria.Label(M, j.Section, j.Code), j.Manual, reason = reasons.GetValueOrDefault(j.Section, "") }).ToList(),
            findings = x.Findings,
            blood = x.Blood,
            metabolic,
            suggest = new { h, c, cCandidates = cCands },
            missing,
            history,
            issued = Db.IssuedPdfs(examId),
            course = M.CourseOf(x.Exam.Course),
        };
    }

    /// <summary>既定値＝正常。コースに含まれる検査の選択項目を、未入力なら正常で埋める。</summary>
    public int FillDefaults(long examId)
    {
        var exam = Db.GetExam(examId); if (exam == null) return 0;
        var secs = M.CourseOf(exam.Course)?.Sections ?? new List<string>();
        var vals = Db.GetValues(examId); var n = 0;
        void Set(string k, string v) { if (!vals.ContainsKey(k)) { Db.SetValue(examId, k, v); n++; } }
        if (secs.Contains(Sections.Hearing)) foreach (var k in new[] { Keys.HearR1000, Keys.HearR4000, Keys.HearL1000, Keys.HearL4000 }) Set(k, "なし");
        if (secs.Contains(Sections.Urine)) { Set(Keys.UrineSugar, "−"); Set(Keys.UrineProtein, "−"); Set(Keys.UrineBlood, "−"); Set(Keys.UrineUrobil, "正常"); }
        if (secs.Contains(Sections.Stool)) { Set(Keys.Stool1, "−"); Set(Keys.Stool2, "−"); }
        return n;
    }

    public object ExamSummary(Exam e)
    {
        var p = Db.GetPatient(e.PatientId);
        var age = p?.AgeAt(DateOnly.TryParse(e.ExamDate, out var d) ? d : DateOnly.FromDateTime(DateTime.Today));
        return new { e.Id, e.PatientId, e.ExamDate, e.Course, e.Status, e.ConfirmedAt, e.UpdatedAt, name = p?.Name ?? "", kana = p?.Kana ?? "", sex = p?.Sex ?? "", age, falcoId = p?.FalcoId ?? "", chartNo = p?.ChartNo ?? "", office = p?.Office ?? "" };
    }
}
