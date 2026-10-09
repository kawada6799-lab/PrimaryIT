namespace Precheck;

/// <summary>患者ページ／予約表の予約 1 件。</summary>
public sealed record Reservation(string Status, DateTime Start, string Department, string Menu, string Memo = "")
{
    public DateOnly Day => DateOnly.FromDateTime(Start);
}

/// <summary>患者。初診で未登録なら CardNo は "(未登録)"。Key は同一患者とみなすキー。</summary>
public sealed record Patient(string CardNo, string Name, string Birth = "", string Key = "")
{
    public string EffectiveKey => string.IsNullOrEmpty(Key) ? $"no:{CardNo}" : Key;
}

/// <summary>結果に載せる 1 件。CheckReasons が空でなければ ※要チェック※ にも載る。</summary>
public sealed record Finding(Patient Patient, Reservation Exam, IReadOnlyList<string> CheckReasons)
{
    public Finding(Patient patient, Reservation exam) : this(patient, exam, Array.Empty<string>()) { }

    /// <summary>同じ患者・同じ検査日に二重に知らせないためのキー。</summary>
    public string Key => $"{Patient.EffectiveKey}:{Exam.Day:yyyy-MM-dd}";
}

/// <summary>予約一覧（日表示）の 1 行。</summary>
public sealed record ScheduleRow(
    DateOnly Day,
    DateTime Start,
    string Status,
    string Menu,
    string Name,
    string Kana,
    string CardNo,
    string Birth,
    string ReservationNo)
{
    /// <summary>同一患者とみなすキー。診察券番号があれば番号、無ければ氏名＋生年月日。</summary>
    public string PatientKey => string.IsNullOrEmpty(CardNo)
        ? $"nm:{TextUtil.NormalizeName(Name)}|{Birth}"
        : $"no:{CardNo}";

    public Reservation ToReservation() => new(Status, Start, "", Menu);
}
