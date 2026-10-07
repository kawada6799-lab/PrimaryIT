"""SMTP でメールを送る。"""
from __future__ import annotations

import smtplib
from email.message import EmailMessage

from .config import MailConfig
from .models import Finding

_WEEKDAY = "月火水木金土日"


def build_message(finding: Finding, cfg: MailConfig) -> EmailMessage:
    p, e = finding.patient, finding.exam
    exam_day = f"{e.start:%Y/%m/%d}({_WEEKDAY[e.start.weekday()]}) {e.start:%H:%M}"
    subject = cfg.subject_template.format(name=p.name, card_no=p.card_no)
    body = (
        f"{p.name}さん（診察券番号 {p.card_no}）は、\n"
        f"{exam_day} の「{e.menu}」の予約がありますが、\n"
        f"大腸カメラ事前診察の予約が見つかりません。\n"
        f"\n"
        f"Wakumy の患者管理で確認のうえ、電話連絡をお願いします。\n"
    )
    if finding.triggered_by:
        body += f"\n（きっかけ: 予約ID {finding.triggered_by.reservation_id} の「{finding.triggered_by.kind}」通知）\n"
    body += "\n-- このメールは precheck（かかりつけIT 案件001）が自動送信しています。AI は使っていません。\n"

    msg = EmailMessage()
    msg["Subject"] = subject
    msg["From"] = cfg.from_addr
    msg["To"] = ", ".join(cfg.to_addrs)
    msg.set_content(body)
    return msg


def send(msg: EmailMessage, cfg: MailConfig) -> None:
    with smtplib.SMTP(cfg.smtp_host, cfg.smtp_port, timeout=30) as s:
        s.ehlo()
        s.starttls()
        s.ehlo()
        if cfg.smtp_user:
            s.login(cfg.smtp_user, cfg.password)
        s.send_message(msg)
