namespace Kenshin.Domain;

public sealed class Patient
{
    public long Id { get; set; }
    public string FalcoId { get; set; } = "";
    public string ChartNo { get; set; } = "";
    public string EmployeeNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kana { get; set; } = "";
    /// <summary>男 / 女</summary>
    public string Sex { get; set; } = "";
    /// <summary>yyyy-MM-dd</summary>
    public string BirthDate { get; set; } = "";
    public string Office { get; set; } = "";
    public string Dept { get; set; } = "";
    public string CreatedAt { get; set; } = "";

    public int? AgeAt(DateOnly on)
    {
        if (!DateOnly.TryParse(BirthDate, out var b)) return null;
        var age = on.Year - b.Year;
        if (on < b.AddYears(age)) age--;
        return age;
    }
}

public static class ExamStatus
{
    public const string Entering = "入力中";
    public const string WaitingFindings = "所見待ち";
    public const string WaitingBlood = "血液待ち";
    public const string Ready = "判定済";
    public const string Confirmed = "確定";
    public const string Mailed = "郵送済";
    public static readonly string[] All = { Entering, WaitingFindings, WaitingBlood, Ready, Confirmed, Mailed };
}

public sealed class Exam
{
    public long Id { get; set; }
    public long PatientId { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    public string ExamDate { get; set; } = "";
    public string Course { get; set; } = "";
    public string Status { get; set; } = ExamStatus.Entering;
    public string? ConfirmedAt { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

/// <summary>項目ごとの判定。Manual が true なら先生が変えたもので、自動判定で上書きしない。</summary>
public sealed class Judgment
{
    public long ExamId { get; set; }
    public string Section { get; set; } = "";
    public string Code { get; set; } = "";
    public bool Manual { get; set; }
    public string UpdatedAt { get; set; } = "";
}

public sealed class Finding
{
    public long Id { get; set; }
    public long ExamId { get; set; }
    public string Section { get; set; } = "";
    /// <summary>所見マスターのコード。自由記述なら空。</summary>
    public string Code { get; set; } = "";
    public string Text { get; set; } = "";
    public int Sort { get; set; }
}

public sealed class BloodResult
{
    public long Id { get; set; }
    public long ExamId { get; set; }
    public string Item { get; set; } = "";
    public string Value { get; set; } = "";
    public string Unit { get; set; } = "";
    public string RefText { get; set; } = "";
    public double? RefLow { get; set; }
    public double? RefHigh { get; set; }
    /// <summary>↑ / ↓ / 空</summary>
    public string Flag { get; set; } = "";
    public string Category { get; set; } = "";
    public int Sort { get; set; }
    public string SourceFile { get; set; } = "";
    public string TakenAt { get; set; } = "";

    public double? NumericValue => Num.Parse(Value);
}

public sealed class LabFile
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string TakenAt { get; set; } = "";
    public string PatientHint { get; set; } = "";
    public long? ExamId { get; set; }
    public string Status { get; set; } = "";
    public string ImportedAt { get; set; } = "";
    public int ItemCount { get; set; }
}

public sealed class EventRecord
{
    public long Id { get; set; }
    public string Ts { get; set; } = "";
    public string Kind { get; set; } = "";
    public long? ExamId { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class IssuedPdf
{
    public long Id { get; set; }
    public long ExamId { get; set; }
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

/// <summary>入力値のキー（exam_values.key）。画面・帳票・判定で共通に使う。</summary>
public static class Keys
{
    public const string Height = "height", Weight = "weight", Waist = "waist";
    public const string VisionR = "vision_r", VisionL = "vision_l", VisionRc = "vision_rc", VisionLc = "vision_lc";
    public const string HearR1000 = "hear_r1000", HearR4000 = "hear_r4000", HearL1000 = "hear_l1000", HearL4000 = "hear_l4000", HearConv = "hear_conv";
    public const string Bp1Sys = "bp1_sys", Bp1Dia = "bp1_dia", Bp2Sys = "bp2_sys", Bp2Dia = "bp2_dia", BpMed = "bp_med";
    public const string UrineSugar = "urine_sugar", UrineProtein = "urine_protein", UrineBlood = "urine_blood", UrineUrobil = "urine_urobil", UrineMenstruating = "urine_menstruating";
    public const string Stool1 = "stool1", Stool2 = "stool2";
    public const string LungVc = "lung_vc", LungPredVc = "lung_pred_vc", LungVcPct = "lung_vc_pct", LungFvc = "lung_fvc", LungFev1 = "lung_fev1", LungFev1Pct = "lung_fev1_pct";
    public const string MealMinutes = "meal_minutes";
    public const string QBpMed = "q_bp_med", QDmMed = "q_dm_med", QLipidMed = "q_lipid_med", QSmoking = "q_smoking",
        QAlcoholFreq = "q_alcohol_freq", QAlcoholAmount = "q_alcohol_amount", QWeightGain = "q_weight_gain10", QExercise = "q_exercise",
        QHistory = "q_history", QSymptoms = "q_symptoms";
    public const string NextGastroYears = "next_gastro_years", NextColonYears = "next_colon_years";
    public const string FreeComment = "free_comment", CommentH = "comment_h", CommentC = "comment_c";
    public const string DoctorName = "doctor_name";
    public const string XrayDirect = "xray_direct", EcgLoad = "ecg_load";
    public static string SectionDate(string section) => "date:" + section;
}

/// <summary>判定の区分（部位）名。</summary>
public static class Sections
{
    public const string Measure = "計測", Vision = "視力", Hearing = "聴力", Bp = "血圧", Urine = "尿検査", Stool = "便検査", Lung = "肺機能";
    public const string ChestXray = "胸部X線", StomachXray = "胃部X線", Ecg = "心電図", Physical = "診察所見", Gastroscopy = "胃内視鏡", Colonoscopy = "大腸内視鏡", Echo = "腹部エコー";
    public const string BloodPrefix = "血液:";
    public const string Metabolic = "メタボ", Overall = "総合";
    /// <summary>所見コードを選ぶ検査（受診日・所見・判定の欄がある）。</summary>
    public static readonly string[] FindingSections = { ChestXray, StomachXray, Ecg, Physical, Gastroscopy, Colonoscopy, Echo };
    /// <summary>受診票・結果のお知らせでの並び。</summary>
    public static readonly string[] Order = { Measure, Vision, Hearing, Bp, Urine, Stool, Lung, ChestXray, StomachXray, Ecg, Physical, Gastroscopy, Colonoscopy, Echo };
}

public static class Num
{
    public static double? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace("，", ",").Replace(",", "");
        t = ToHalfWidth(t);
        // 「0.1以下」「<= 0.05」「0.05未満」などは数値部分だけ取る
        var digits = new string(t.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        if (digits.Length == 0) return null;
        return double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static string ToHalfWidth(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= '０' && c <= '９') sb.Append((char)('0' + (c - '０')));
            else if (c == '．') sb.Append('.');
            else if (c == '－') sb.Append('-');
            else if (c >= 'Ａ' && c <= 'Ｚ') sb.Append((char)('A' + (c - 'Ａ')));
            else if (c >= 'ａ' && c <= 'ｚ') sb.Append((char)('a' + (c - 'ａ')));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Fmt(double? v, int decimals = 1) =>
        v == null ? "" : v.Value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
}
