from datetime import datetime

from precheck.config import MailConfig
from precheck.mailer import build_message
from precheck.models import Finding, Notification, Patient, Reservation
from precheck.state import State


def test_state_roundtrip(tmp_path):
    path = tmp_path / "s" / "processed.json"
    st = State(path)
    assert not st.is_processed("100")
    st.mark_processed("100", "テスト 太郎")
    st.mark_notified("1:2026-10-31")
    st.save()

    st2 = State(path)
    assert st2.is_processed("100")
    assert st2.is_notified("1:2026-10-31")
    assert not st2.is_notified("1:2026-11-01")


def test_build_message_contents():
    cfg = MailConfig(from_addr="bot@example.com", to_addrs=["a@example.com", "b@example.com"])
    patient = Patient(card_no="535", name="テスト 太郎")
    exam = Reservation("予約", datetime(2026, 10, 31, 13, 30), "内視鏡", "胃＋大腸カメラ検査")
    note = Notification("15421790", "テスト 太郎", "予約確定時", datetime(2026, 10, 7, 21, 53, 23))
    msg = build_message(Finding(patient, exam, note), cfg)
    assert msg["Subject"] == "【要確認】テスト 太郎さん 大腸カメラ事前診察の予約なし"
    assert msg["To"] == "a@example.com, b@example.com"
    body = msg.get_content()
    assert "診察券番号 535" in body
    assert "2026/10/31(土) 13:30" in body
    assert "胃＋大腸カメラ検査" in body
    assert "15421790" in body
