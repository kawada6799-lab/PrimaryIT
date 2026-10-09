"""判定ロジック。Wakumy にも Playwright にも依存しない純粋関数だけを置く。"""
from __future__ import annotations

from datetime import date, timedelta

from .config import Rules
from .models import Finding, Notification, Patient, Reservation
from .schedule import ScheduleRow
from .parse import normalize


def _contains_any(text: str, keywords: list[str]) -> bool:
    t = normalize(text)
    return any(normalize(k) in t for k in keywords)


def is_exam(r: Reservation, rules: Rules) -> bool:
    """大腸の検査予約（事前診察の確認対象）か。"""
    return (
        r.status in rules.exam_active_statuses
        and _contains_any(r.menu, rules.exam_keywords)
        and not _contains_any(r.menu, rules.exam_exclude_keywords)
    )


def is_pre_exam(r: Reservation, rules: Rules) -> bool:
    """大腸カメラ事前診察の予約（または受診済み）か。"""
    return r.status in rules.pre_exam_ok_statuses and _contains_any(r.menu, rules.pre_exam_keywords)


def has_pre_exam_for(exam: Reservation, reservations: list[Reservation], rules: Rules) -> bool:
    """検査日の前 window_days 日以内（当日含む）に事前診察があるか。"""
    lo = exam.day - timedelta(days=rules.window_days)
    hi = exam.day
    return any(
        is_pre_exam(r, rules) and lo <= r.day <= hi
        for r in reservations
    )


def upcoming_exams(patient: Patient, rules: Rules, today: date) -> list[Reservation]:
    """今日以降の検査予約を日付順に返す。"""
    exams = [r for r in patient.reservations if is_exam(r, rules) and r.day >= today]
    return sorted(exams, key=lambda r: r.start)


def find_missing_pre_exam(
    patient: Patient,
    rules: Rules,
    today: date,
    triggered_by: Notification | None = None,
) -> list[Finding]:
    """事前診察が無い検査予約を Finding にして返す。"""
    return [
        Finding(patient=patient, exam=exam, triggered_by=triggered_by)
        for exam in upcoming_exams(patient, rules, today)
        if not has_pre_exam_for(exam, patient.reservations, rules)
    ]


def select_notifications(
    notifications: list[Notification], rules: Rules, today: date
) -> list[Notification]:
    """対象の通知種別で、古すぎないものだけ残す。"""
    cutoff = today - timedelta(days=rules.notification_max_age_days)
    out = []
    for n in notifications:
        if normalize(n.kind) != normalize(rules.notification_kind):
            continue
        if n.completed_at is not None and n.completed_at.date() < cutoff:
            continue
        out.append(n)
    return out


# ---------- 予約表（内視鏡タブ／外来診察タブ）ベースの判定 ----------

def schedule_row_to_reservation(r: ScheduleRow) -> Reservation:
    return Reservation(status=r.status, start=r.start, department="", menu=r.menu)


def find_missing_pre_exam_from_schedule(
    exam_rows: list[ScheduleRow],
    pre_exam_rows: list[ScheduleRow],
    rules: Rules,
    today: date,
) -> list[tuple[ScheduleRow, Patient, Reservation]]:
    """内視鏡タブで集めた予約と、外来診察タブで集めた事前診察を患者キーで突き合わせる。

    返り値は (検査の行, 患者, 検査の Reservation)。患者キーは診察券番号、無ければ氏名＋生年月日。
    """
    pre_by_patient: dict[str, list[Reservation]] = {}
    for r in pre_exam_rows:
        res = schedule_row_to_reservation(r)
        if is_pre_exam(res, rules):
            pre_by_patient.setdefault(r.patient_key, []).append(res)

    out: list[tuple[ScheduleRow, Patient, Reservation]] = []
    seen: set[tuple[str, date]] = set()
    for r in sorted(exam_rows, key=lambda x: x.start):
        exam = schedule_row_to_reservation(r)
        if not is_exam(exam, rules) or r.day < today:
            continue
        key = (r.patient_key, r.day)
        if key in seen:
            continue  # 同じ日に胃＋大腸と連鎖用など複数行あっても1件
        seen.add(key)
        if has_pre_exam_for(exam, pre_by_patient.get(r.patient_key, []), rules):
            continue
        patient = Patient(card_no=r.card_no or "(未登録)", name=r.name, birth=r.birth, key=r.patient_key)
        out.append((r, patient, exam))
    return out


def flag_needs_check(
    findings: list[Finding],
    outpatient_rows: list[ScheduleRow],
    today: date,
    recent_days: int,
) -> list[Finding]:
    """結果の各患者に ※要チェック※ の理由を付ける。

    - 過去 recent_days 日以内に外来診察タブに一度も出てこない（キャンセル行は除く）
    - 診察券番号が未登録
    """
    lo = today - timedelta(days=recent_days)
    seen_recently: set[str] = set()
    for r in outpatient_rows:
        if lo <= r.day <= today and "キャンセル" not in r.status:
            seen_recently.add(r.patient_key)

    out: list[Finding] = []
    for f in findings:
        reasons: list[str] = []
        key = f.patient.key or f"no:{f.patient.card_no}"
        if key not in seen_recently:
            reasons.append(f"過去{recent_days}日に外来受診なし")
        if f.patient.card_no == "(未登録)":
            reasons.append("診察券番号が未登録")
        out.append(Finding(patient=f.patient, exam=f.exam, triggered_by=f.triggered_by, check_reasons=tuple(reasons)))
    return out
