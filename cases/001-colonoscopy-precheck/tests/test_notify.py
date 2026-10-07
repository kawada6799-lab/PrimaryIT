from datetime import date, datetime

from precheck.models import Finding, Patient, Reservation
from precheck.notify import build_report, write_report

TODAY = date(2026, 10, 8)


def _finding(card, name, y, m, d, menu):
    return Finding(Patient(card_no=card, name=name), Reservation("予約", datetime(y, m, d, 13, 30), "内視鏡", menu))


def test_build_report_lists_card_no_and_name_sorted_by_exam_date():
    fs = [
        _finding("535", "テスト 太郎", 2026, 11, 2, "胃＋大腸カメラ検査"),
        _finding("12", "テスト 花子", 2026, 10, 31, "大腸カメラ検査"),
    ]
    text = build_report(fs, TODAY)
    assert "2 名" in text
    lines = [l for l in text.splitlines() if l.startswith(("535", "12 "))]
    assert lines[0].startswith("12")       # 検査日が早い方が先
    assert "テスト 花子" in lines[0] and "10/31(土) 13:30" in lines[0]
    assert "テスト 太郎" in lines[1] and "胃＋大腸カメラ検査" in lines[1]


def test_build_report_empty():
    text = build_report([], TODAY)
    assert "0 名" in text


def test_write_report_creates_bom_utf8_file(tmp_path):
    path = write_report("テスト\n", tmp_path / "結果", TODAY)
    assert path.name == "大腸カメラ事前診察なし_2026-10-08.txt"
    raw = path.read_bytes()
    assert raw.startswith(b"\xef\xbb\xbf")
    assert raw.decode("utf-8-sig") == "テスト\n"


def test_build_report_lists_not_found_with_reservation_id():
    from precheck.models import Notification
    nf = [Notification("15421386", "テスト 次郎", "予約確定時", None)]
    text = build_report([], TODAY, nf)
    assert "判定できなかった予約（1 件）" in text
    assert "15421386" in text and "テスト 次郎" in text
