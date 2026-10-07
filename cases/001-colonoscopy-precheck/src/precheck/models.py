from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, datetime


@dataclass(frozen=True)
class Notification:
    """予約通知一覧の1行。"""

    reservation_id: str
    patient_name: str
    kind: str                      # 通知種別（例: 予約確定時）
    completed_at: datetime | None  # 通知完了日時


@dataclass(frozen=True)
class Reservation:
    """患者ページの予約一覧の1行。"""

    status: str          # 予約 / 来院 / 患者都合キャンセル など
    start: datetime      # 予約開始日時
    department: str      # 診療科目
    menu: str            # 予約メニュー
    memo: str = ""

    @property
    def day(self) -> date:
        return self.start.date()


@dataclass
class Patient:
    """患者ページから読み取った情報。"""

    card_no: str                 # 診察券番号
    name: str
    reservations: list[Reservation] = field(default_factory=list)


@dataclass(frozen=True)
class Finding:
    """メールで知らせる1件。"""

    patient: Patient
    exam: Reservation
    triggered_by: Notification | None = None

    @property
    def key(self) -> str:
        """同じ患者・同じ検査日に対して二重に知らせないためのキー。"""
        return f"{self.patient.card_no}:{self.exam.day.isoformat()}"
