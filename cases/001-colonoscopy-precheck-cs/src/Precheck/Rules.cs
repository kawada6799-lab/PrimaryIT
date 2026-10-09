namespace Precheck;

/// <summary>判定ロジック。画面にも Playwright にも依存しない。</summary>
public static class Rules
{
    public static bool IsExam(Reservation r, RulesConfig rules) =>
        rules.ExamActiveStatuses.Contains(r.Status)
        && TextUtil.ContainsAny(r.Menu, rules.ExamKeywords)
        && !TextUtil.ContainsAny(r.Menu, rules.ExamExcludeKeywords);

    public static bool IsPreExam(Reservation r, RulesConfig rules) =>
        rules.PreExamOkStatuses.Contains(r.Status) && TextUtil.ContainsAny(r.Menu, rules.PreExamKeywords);

    /// <summary>検査日の前 WindowDays 日以内（当日含む）に事前診察があるか。</summary>
    public static bool HasPreExamFor(Reservation exam, IEnumerable<Reservation> reservations, RulesConfig rules)
    {
        var lo = exam.Day.AddDays(-rules.WindowDays);
        var hi = exam.Day;
        return reservations.Any(r => IsPreExam(r, rules) && r.Day >= lo && r.Day <= hi);
    }

    /// <summary>
    /// 内視鏡タブで集めた予約と、外来診察タブで集めた事前診察を患者キーで突き合わせる。
    /// 返り値は事前診察の無い検査予約。
    /// </summary>
    public static List<Finding> FindMissingPreExamFromSchedule(
        IEnumerable<ScheduleRow> examRows, IEnumerable<ScheduleRow> preExamRows, RulesConfig rules, DateOnly today)
    {
        var preByPatient = new Dictionary<string, List<Reservation>>();
        foreach (var r in preExamRows)
        {
            var res = r.ToReservation();
            if (!IsPreExam(res, rules)) continue;
            if (!preByPatient.TryGetValue(r.PatientKey, out var list)) preByPatient[r.PatientKey] = list = new();
            list.Add(res);
        }

        var seen = new HashSet<(string, DateOnly)>();
        var outList = new List<Finding>();
        foreach (var r in examRows.OrderBy(x => x.Start))
        {
            var exam = r.ToReservation();
            if (!IsExam(exam, rules) || r.Day < today) continue;
            if (!seen.Add((r.PatientKey, r.Day))) continue; // 同じ日に胃＋大腸と連鎖用など複数行あっても 1 件
            var pres = preByPatient.TryGetValue(r.PatientKey, out var l) ? l : new List<Reservation>();
            if (HasPreExamFor(exam, pres, rules)) continue;
            var patient = new Patient(r.CardNo.Length == 0 ? "(未登録)" : r.CardNo, r.Name, r.Birth, r.PatientKey);
            outList.Add(new Finding(patient, exam));
        }
        return outList;
    }

    /// <summary>
    /// ※要チェック※ の理由を付ける：過去 recentDays 日に外来診察タブに出てこない（キャンセル除く）／診察券番号が未登録。
    /// </summary>
    public static List<Finding> FlagNeedsCheck(
        IEnumerable<Finding> findings, IEnumerable<ScheduleRow> outpatientRows, DateOnly today, int recentDays)
    {
        var lo = today.AddDays(-recentDays);
        var seenRecently = outpatientRows
            .Where(r => r.Day >= lo && r.Day <= today && !r.Status.Contains("キャンセル"))
            .Select(r => r.PatientKey)
            .ToHashSet();

        return findings.Select(f =>
        {
            var reasons = new List<string>();
            if (!seenRecently.Contains(f.Patient.EffectiveKey)) reasons.Add($"過去{recentDays}日に外来受診なし");
            if (f.Patient.CardNo == "(未登録)") reasons.Add("診察券番号が未登録");
            return new Finding(f.Patient, f.Exam, reasons);
        }).ToList();
    }

    /// <summary>今後の大腸検査の件数（患者×検査日で数える。ログ表示用）。</summary>
    public static int CountUpcomingExams(IEnumerable<ScheduleRow> examRows, RulesConfig rules, DateOnly today) =>
        examRows.Where(r => IsExam(r.ToReservation(), rules) && r.Day >= today)
                .Select(r => (r.PatientKey, r.Day)).Distinct().Count();
}
