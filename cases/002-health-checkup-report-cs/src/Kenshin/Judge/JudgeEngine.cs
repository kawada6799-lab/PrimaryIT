using Kenshin.Config;
using Kenshin.Domain;

namespace Kenshin.Judge;

/// <summary>判定に必要な 1 回分の材料。</summary>
public sealed class ExamContext
{
    public required Patient Patient { get; init; }
    public required Exam Exam { get; init; }
    public required Dictionary<string, string> Values { get; init; }
    public List<Finding> Findings { get; init; } = new();
    public List<BloodResult> Blood { get; init; } = new();
    /// <summary>既存の判定（手動のものは残す）。</summary>
    public List<Judgment> Existing { get; init; } = new();

    public string V(string key) => Values.TryGetValue(key, out var v) ? v : "";
    public double? N(string key) => Num.Parse(V(key));
    public bool Has(string key) => !string.IsNullOrWhiteSpace(V(key));
    public bool IsMale => Patient.Sex == "男";
}

/// <summary>自動判定の結果（部位ごとのコードと、理由）。</summary>
public sealed record SectionResult(string Section, string? Code, string Reason);

/// <summary>決定的なルールで判定する。AI は使わない。境界はすべて Criteria（判定基準.json）。</summary>
public sealed class JudgeEngine
{
    readonly Criteria _c;
    readonly Masters _m;
    public JudgeEngine(Criteria criteria, Masters masters) { _c = criteria; _m = masters; }

    public static double? Bmi(double? height, double? weight)
    {
        if (height is null or <= 0 || weight is null) return null;
        var m = height.Value / 100.0;
        return Math.Round(weight.Value / (m * m), 1);
    }
    public double? StandardWeight(double? height)
    {
        if (height is null or <= 0) return null;
        var m = height.Value / 100.0;
        return Math.Round(_c.Bmi.StandardWeightBmi * m * m, 1);
    }
    /// <summary>肥満度（%）＝（体重−標準体重）／標準体重×100</summary>
    public double? Obesity(double? height, double? weight)
    {
        var sw = StandardWeight(height);
        if (sw is null or 0 || weight is null) return null;
        return Math.Round((weight.Value - sw.Value) / sw.Value * 100.0, 1);
    }

    // ---- 計測 ----
    public SectionResult JudgeMeasure(ExamContext x)
    {
        var bmi = Bmi(x.N(Keys.Height), x.N(Keys.Weight));
        if (bmi == null) return new(Sections.Measure, null, "身長・体重が未入力");
        string code = "150"; var reasons = new List<string>();
        foreach (var t in _c.Bmi.Tiers)
        {
            if ((t.Min == null || bmi >= t.Min) && (t.Max == null || bmi <= t.Max)) { code = t.Code; break; }
        }
        reasons.Add($"BMI {bmi:F1}");
        var waist = x.N(Keys.Waist);
        if (waist != null)
        {
            var limit = x.IsMale ? _c.Waist.Male : _c.Waist.Female;
            if (waist >= limit)
            {
                // 肥満度の個別コードと腹囲の区分は、総合判定に数えるときの重さで比べる
                if (_m.Rank(_c.Waist.Code) > _m.Rank(Sections.Measure, code)) code = _c.Waist.Code;
                reasons.Add($"腹囲 {waist:F1}（{limit:F0} 以上）");
            }
        }
        return new(Sections.Measure, code, string.Join("、", reasons));
    }

    // ---- 視力 ----
    public SectionResult JudgeVision(ExamContext x)
    {
        // 視力の個別コード：裸眼で両眼 normalMin 以上 → 異常なし。矯正値があり両眼 normalMin 以上 → 眼鏡あり。矯正しても未満 → 視力再矯正。裸眼で未満かつ矯正なし → 視力矯正
        double? nr = x.N(Keys.VisionR), nl = x.N(Keys.VisionL), cr = x.N(Keys.VisionRc), cl = x.N(Keys.VisionLc);
        if (nr == null && nl == null && cr == null && cl == null) return new(Sections.Vision, null, "未入力");
        var min = _c.Vision.NormalMin;
        var nakedOk = (nr == null || nr >= min) && (nl == null || nl >= min) && (nr != null || nl != null);
        var hasCorr = cr != null || cl != null;
        if (!hasCorr) return new(Sections.Vision, nakedOk ? _c.Vision.Normal : _c.Vision.Correction, nakedOk ? $"裸眼 両眼 {min} 以上" : $"裸眼 {min} 未満あり（矯正なし）");
        double? r = cr ?? nr, l = cl ?? nl;
        var corrOk = (r == null || r >= min) && (l == null || l >= min);
        if (corrOk) return new(Sections.Vision, nakedOk ? _c.Vision.Normal : _c.Vision.Glasses, nakedOk ? $"裸眼 両眼 {min} 以上" : $"矯正で両眼 {min} 以上");
        return new(Sections.Vision, _c.Vision.Recorrection, $"矯正しても {min} 未満あり");
    }

    // ---- 聴力 ----
    public SectionResult JudgeHearing(ExamContext x)
    {
        var keys = new[] { Keys.HearR1000, Keys.HearR4000, Keys.HearL1000, Keys.HearL4000 };
        var vals = keys.Select(x.V).ToList();
        if (vals.All(string.IsNullOrWhiteSpace)) return new(Sections.Hearing, null, "未入力");
        var any = vals.Any(v => v == "あり");
        return new(Sections.Hearing, any ? _c.Hearing.Code : "150", any ? "所見あり" : "所見なし");
    }

    // ---- 血圧 ----
    public (int sys, int dia)? BpToUse(ExamContext x)
    {
        var s1 = x.N(Keys.Bp1Sys); var d1 = x.N(Keys.Bp1Dia); var s2 = x.N(Keys.Bp2Sys); var d2 = x.N(Keys.Bp2Dia);
        var first = s1 != null && d1 != null ? ((int)s1, (int)d1) : ((int, int)?)null;
        var second = s2 != null && d2 != null ? ((int)s2, (int)d2) : ((int, int)?)null;
        switch (_c.BloodPressure.Use)
        {
            case "first": return first ?? second;
            case "lower":
                if (first != null && second != null) return first.Value.Item1 <= second.Value.Item1 ? first : second;
                return first ?? second;
            case "average":
                if (first != null && second != null) return ((first.Value.Item1 + second.Value.Item1) / 2, (first.Value.Item2 + second.Value.Item2) / 2);
                return first ?? second;
            default: return second ?? first;
        }
    }
    public SectionResult JudgeBp(ExamContext x)
    {
        var bp = BpToUse(x);
        if (bp == null) return new(Sections.Bp, null, "未入力");
        string? code = null;
        foreach (var t in _c.BloodPressure.Tiers)
        {
            if ((t.SysMax == null || bp.Value.sys <= t.SysMax) && (t.DiaMax == null || bp.Value.dia <= t.DiaMax)) { code = t.Code; break; }
        }
        code ??= "150";
        var reason = $"{bp.Value.sys}/{bp.Value.dia}";
        if ((x.V(Keys.BpMed) == "1" || x.V(Keys.QBpMed) == "1") && !string.IsNullOrEmpty(_c.BloodPressure.OnMedicationCode))
        {
            code = _m.Heaviest(new[] { code, _c.BloodPressure.OnMedicationCode }) ?? code;
            reason += "、降圧剤服用";
        }
        return new(Sections.Bp, code, reason);
    }

    // ---- 尿 ----
    public SectionResult JudgeUrine(ExamContext x)
    {
        var map = new (string key, Dictionary<string, string> table, string label)[]
        {
            (Keys.UrineSugar, _c.Urine.Sugar, "糖"), (Keys.UrineProtein, _c.Urine.Protein, "蛋白"),
            (Keys.UrineBlood, _c.Urine.Blood, "潜血"), (Keys.UrineUrobil, _c.Urine.Urobilinogen, "ウロビリノーゲン"),
        };
        var codes = new List<string>(); var reasons = new List<string>(); var any = false;
        foreach (var (key, table, label) in map)
        {
            var v = x.V(key);
            if (string.IsNullOrWhiteSpace(v)) continue;
            any = true;
            if (table.TryGetValue(v, out var c) && c != "150") { codes.Add(c); reasons.Add($"{label}（{v}）"); }
        }
        if (!any) return new(Sections.Urine, null, "未入力");
        var code = _m.Heaviest(codes) ?? "150";
        return new(Sections.Urine, code, reasons.Count == 0 ? "すべて（−）" : string.Join("、", reasons));
    }

    // ---- 便 ----
    public SectionResult JudgeStool(ExamContext x)
    {
        var a = x.V(Keys.Stool1); var b = x.V(Keys.Stool2);
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return new(Sections.Stool, null, "未入力");
        var pos = a == "+" || b == "+";
        return new(Sections.Stool, pos ? _c.Stool.Code : "150", pos ? "潜血（+）あり" : "（−）（−）");
    }

    // ---- 肺機能 ----
    public SectionResult JudgeLung(ExamContext x)
    {
        var fev = x.N(Keys.LungFev1Pct); var vc = x.N(Keys.LungVcPct);
        if (fev == null && vc == null) return new(Sections.Lung, null, "未入力");
        var bad = (fev != null && fev < _c.LungFunction.Fev1PercentMin) || (vc != null && vc < _c.LungFunction.VcPercentMin);
        return new(Sections.Lung, bad ? _c.LungFunction.Code : "150", bad ? "基準未満あり" : "基準内");
    }

    // ---- 所見のある検査（X 線・心電図・診察・内視鏡・エコー）----
    public SectionResult JudgeFindingSection(ExamContext x, string section)
    {
        var fs = x.Findings.Where(f => f.Section == section).ToList();
        if (fs.Count == 0) return new(section, null, "所見が未入力");
        // 所見マスターに判定があればそれ。無ければ名称が「異常なし」系なら normalCode、それ以外は defaultCode（仮。先生が変える）
        var codes = new List<string>(); var usedDefault = false;
        foreach (var f in fs)
        {
            var mj = _m.Finding(section, f.Code)?.Judgment;
            if (!string.IsNullOrEmpty(mj)) { codes.Add(mj); continue; }
            var t = f.Text ?? "";
            if (_c.Findings.NormalPatterns.Any(p => t.Contains(p))) codes.Add(_c.Findings.NormalCode);
            else { codes.Add(_c.Findings.DefaultCode); usedDefault = true; }
        }
        var code = _m.Heaviest(codes) ?? _c.Findings.NormalCode;
        return new(section, code, string.Join("、", fs.Select(f => f.Text)) + (usedDefault ? $"（所見ありは仮に{_m.CategoryName(_c.Findings.DefaultCode)}。先生が変える）" : ""));
    }

    // ---- 血液（分野ごと）----
    public bool IsOutOfRange(BloodResult r)
    {
        if (r.Flag is "↑" or "↓" or "H" or "L") return true;
        var v = r.NumericValue;
        if (v == null) return false;
        if (r.RefLow != null && v < r.RefLow) return true;
        if (r.RefHigh != null && v > r.RefHigh) return true;
        return false;
    }
    public List<SectionResult> JudgeBlood(ExamContext x)
    {
        var results = new List<SectionResult>();
        if (x.Blood.Count == 0) return results;
        foreach (var r in x.Blood) if (string.IsNullOrEmpty(r.Category)) r.Category = _c.Blood.CategoryOf(r.Item);
        var cats = _c.Blood.JudgedCategories.Count > 0 ? _c.Blood.JudgedCategories : x.Blood.Select(b => b.Category).Distinct().ToList();
        foreach (var cat in cats)
        {
            var items = x.Blood.Where(b => b.Category == cat).ToList();
            if (items.Count == 0) continue;
            var codes = new List<string>(); var reasons = new List<string>();
            var oor = _c.Blood.CategoryOverride.TryGetValue(cat, out var o) ? o : _c.Blood.OutOfRangeCode;
            foreach (var it in items)
            {
                if (IsOutOfRange(it)) { codes.Add(oor); reasons.Add($"{it.Item} {it.Value}{it.Flag}"); }
                var v = it.NumericValue;
                if (v == null) continue;
                foreach (var s in _c.Blood.Severe)
                {
                    if (!BloodCriteria.ContainsKey(it.Item, s.Item)) continue;
                    if ((s.Min != null && v >= s.Min) || (s.Max != null && v <= s.Max)) codes.Add(s.Code);
                }
            }
            var code = _m.Heaviest(codes) ?? "150";
            results.Add(new(Sections.BloodPrefix + cat, code, reasons.Count == 0 ? "基準値内" : string.Join("、", reasons)));
        }
        return results;
    }

    // ---- メタボ ----
    public sealed record MetabolicResult(string Label, bool? WaistOver, bool? Bp, bool? Lipid, bool? Glucose, string Comment, string Detail);
    public MetabolicResult JudgeMetabolic(ExamContext x)
    {
        var mc = _c.Metabolic;
        var waist = x.N(Keys.Waist);
        bool? waistOver = waist == null ? null : waist >= (x.IsMale ? mc.Waist.Male : mc.Waist.Female);
        var bp = BpToUse(x);
        bool? bpHit = bp == null ? null : (bp.Value.sys >= mc.Bp.Sys || bp.Value.dia >= mc.Bp.Dia || x.V(Keys.QBpMed) == "1" || x.V(Keys.BpMed) == "1");
        double? tg = BloodValue(x, "中性脂肪"), hdl = BloodValue(x, "HDL"), fbs = BloodValue(x, "血糖"), a1c = BloodValue(x, "HbA1c");
        bool? lipid = (tg == null && hdl == null) ? null : ((tg != null && tg >= mc.Lipid.TgMin) || (hdl != null && hdl < mc.Lipid.HdlMax) || x.V(Keys.QLipidMed) == "1");
        bool? glucose = (fbs == null && a1c == null) ? null : ((fbs != null && fbs >= mc.Glucose.FbsMin) || (a1c != null && a1c >= mc.Glucose.Hba1cMin) || x.V(Keys.QDmMed) == "1");
        string key;
        if (waistOver == null || (lipid == null && glucose == null && bpHit == null)) key = "判定不能";
        else if (waistOver == false) key = "非該当";
        else
        {
            var n = new[] { bpHit, lipid, glucose }.Count(b => b == true);
            key = n >= 2 ? "該当" : n == 1 ? "予備群" : "非該当";
        }
        var label = mc.Labels.TryGetValue(key, out var l) ? l : key;
        var comment = mc.Comments.TryGetValue(key, out var cm) ? cm : "";
        var detail = $"腹囲 {Num.Fmt(waist)}（{(waistOver == true ? "基準以上" : waistOver == false ? "基準未満" : "未入力")}）、血圧 {(bp == null ? "未入力" : bp.Value.sys + "/" + bp.Value.dia)}（{Hit(bpHit)}）、脂質 TG {Num.Fmt(tg, 0)} HDL {Num.Fmt(hdl, 0)}（{Hit(lipid)}）、血糖 {Num.Fmt(fbs, 0)} HbA1c {Num.Fmt(a1c)}（{Hit(glucose)}）";
        return new(label, waistOver, bpHit, lipid, glucose, comment, detail);
        static string Hit(bool? b) => b == true ? "該当" : b == false ? "非該当" : "未入力";
    }
    static double? BloodValue(ExamContext x, string contains)
    {
        var r = x.Blood.FirstOrDefault(b => BloodCriteria.ContainsKey(b.Item, contains) && b.NumericValue != null);
        return r?.NumericValue;
    }

    // ---- 全部まとめて ----
    /// <summary>自動判定を計算し、既存の手動判定は残して返す。</summary>
    public List<Judgment> ComputeAll(ExamContext x, string now)
    {
        var auto = new List<SectionResult>
        {
            JudgeMeasure(x), JudgeVision(x), JudgeHearing(x), JudgeBp(x), JudgeUrine(x), JudgeStool(x), JudgeLung(x),
        };
        foreach (var s in Sections.FindingSections) auto.Add(JudgeFindingSection(x, s));
        auto.AddRange(JudgeBlood(x));

        var result = new List<Judgment>();
        foreach (var r in auto)
        {
            var existing = x.Existing.FirstOrDefault(j => j.Section == r.Section);
            if (existing != null && existing.Manual) { result.Add(existing); continue; }
            if (r.Code == null) continue;
            result.Add(new Judgment { ExamId = x.Exam.Id, Section = r.Section, Code = r.Code, Manual = false, UpdatedAt = now });
        }
        // 総合判定：手動があればそれ、無ければ最も重いもの
        var overallExisting = x.Existing.FirstOrDefault(j => j.Section == Sections.Overall);
        if (overallExisting != null && overallExisting.Manual) result.Add(overallExisting);
        else
        {
            var heaviest = _m.Heaviest(result.Select(j => _m.OverallCode(j.Section, j.Code)));
            if (heaviest != null) result.Add(new Judgment { ExamId = x.Exam.Id, Section = Sections.Overall, Code = heaviest, Manual = false, UpdatedAt = now });
        }
        return result;
    }

    /// <summary>総合判定から判定コメント H の既定、問診から C の候補。</summary>
    public (string h, string c, List<string> cCandidates) SuggestComments(ExamContext x, string? overallCode)
    {
        var h = overallCode != null && _c.Overall.HDefault.TryGetValue(overallCode, out var hh) ? hh : "H001";
        var candidates = new List<string>();
        var bmi = Bmi(x.N(Keys.Height), x.N(Keys.Weight));
        foreach (var rule in _c.Overall.CRules)
        {
            var hit = rule.When switch
            {
                "smoking" => x.V(Keys.QSmoking) == "1",
                "bmi>=25" => bmi != null && bmi >= 25,
                "alcohol_high" => IsAlcoholHigh(x),
                _ => false,
            };
            if (hit) candidates.Add(rule.Code);
        }
        var c = candidates.FirstOrDefault() ?? _c.Overall.CDefault;
        if (!candidates.Contains(_c.Overall.CDefault)) candidates.Add(_c.Overall.CDefault);
        return (h, c, candidates);
    }
    static bool IsAlcoholHigh(ExamContext x)
    {
        var freq = x.V(Keys.QAlcoholFreq); var amt = x.V(Keys.QAlcoholAmount);
        if (amt.Contains("3合")) return true;
        return freq == "毎日" && amt.Contains("2");
    }

    /// <summary>入力が揃っているか（コースの必須検査）。足りない検査名を返す。</summary>
    public List<string> MissingRequired(ExamContext x, List<Judgment> judgments)
    {
        var course = _m.CourseOf(x.Exam.Course);
        if (course == null) return new();
        var done = judgments.Select(j => j.Section).ToHashSet();
        var hasBlood = judgments.Any(j => j.Section.StartsWith(Sections.BloodPrefix));
        var missing = new List<string>();
        foreach (var s in course.Required)
        {
            if (s == "血液検査") { if (!hasBlood) missing.Add(s); }
            else if (!done.Contains(s)) missing.Add(s);
        }
        return missing;
    }
}
