"""判定ロジック。Wakumy にも Playwright にも依存しない純粋関数だけを置く。"""
from __future__ import annotations

from datetime import date, timedelta

from .config import Rules
from .models import Finding, Notification, Patient, Reservation
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
