from datetime import date, datetime

from precheck.config import Rules
from precheck.models import Notification, Patient, Reservation
from precheck.rules import find_missing_pre_exam, has_pre_exam_for, is_exam, is_pre_exam, select_notifications

TODAY = date(2026, 10, 8)


def r(status, y, m, d, menu, dept="内視鏡", hh=14):
    return Reservation(status=status, start=datetime(y, m, d, hh, 0), department=dept, menu=menu)


def test_is_exam_matches_colonoscopy_menus_only():
    rules = Rules()
    assert is_exam(r("予約", 2026, 10, 31, "胃＋大腸カメラ検査"), rules)
    assert is_exam(r("予約", 2026, 10, 31, "大腸カメラ検査"), rules)
    assert not is_exam(r("予約", 2026, 10, 31, "胃カメラ検査"), rules)
    # 連鎖予約の枠確保用メニューと事前診察そのものは検査ではない
    assert not is_exam(r("予約", 2026, 10, 31, "連鎖用(胃+大腸)"), rules)
    assert not is_exam(r("予約", 2026, 10, 9, "大腸カメラ事前診察", dept="外来診察"), rules)
    # キャンセル済みは数えない
    assert not is_exam(r("患者都合キャンセル", 2026, 10, 24, "胃＋大腸カメラ検査"), rules)


def test_is_pre_exam_accepts_booked_and_visited():
    rules = Rules()
    assert is_pre_exam(r("予約", 2026, 10, 9, "大腸カメラ事前診察", dept="外来診察"), rules)
    assert is_pre_exam(r("来院", 2026, 10, 9, "大腸カメラ事前診察", dept="外来診察"), rules)
    assert not is_pre_exam(r("患者都合キャンセル", 2026, 10, 9, "大腸カメラ事前診察"), rules)
    assert not is_pre_exam(r("予約", 2026, 10, 9, "外来診察再診"), rules)


def test_has_pre_exam_window():
    rules = Rules(window_days=30)
    exam = r("予約", 2026, 10, 31, "胃＋大腸カメラ検査")
    ok = r("予約", 2026, 10, 9, "大腸カメラ事前診察", dept="外来診察")      # 22日前
    too_old = r("来院", 2026, 9, 1, "大腸カメラ事前診察", dept="外来診察")  # 60日前
    after = r("予約", 2026, 11, 2, "大腸カメラ事前診察", dept="外来診察")   # 検査の後
    assert has_pre_exam_for(exam, [ok], rules)
    assert not has_pre_exam_for(exam, [too_old], rules)
    assert not has_pre_exam_for(exam, [after], rules)
    assert not has_pre_exam_for(exam, [], rules)


def test_find_missing_pre_exam_screenshot_case_is_ok():
    """先生の画面例：10/31 の胃＋大腸カメラ検査に対し 10/9 の事前診察あり → 通知しない。"""
    patient = Patient(card_no="535", name="テスト 太郎", reservations=[
        r("予約", 2026, 10, 31, "連鎖用（内視鏡）", dept="外来診察"),
        r("予約", 2026, 10, 31, "連鎖用(胃+大腸)"),
        r("予約", 2026, 10, 31, "胃＋大腸カメラ検査", hh=13),
        r("患者都合キャンセル", 2026, 10, 24, "連鎖用(胃+大腸)"),
        r("患者都合キャンセル", 2026, 10, 24, "胃＋大腸カメラ検査", hh=13),
        r("予約", 2026, 10, 9, "大腸カメラ事前診察", dept="外来診察", hh=11),
        r("来院", 2026, 2, 16, "外来診察初診", dept="外来診察", hh=10),
        r("来院", 2025, 6, 28, "胃カメラ検査", hh=10),
    ])
    assert find_missing_pre_exam(patient, Rules(), TODAY) == []


def test_find_missing_pre_exam_reports_when_absent():
    patient = Patient(card_no="1", name="テスト 花子", reservations=[
        r("予約", 2026, 10, 31, "胃＋大腸カメラ検査"),
        r("予約", 2026, 10, 31, "連鎖用(胃+大腸)"),
        r("来院", 2026, 2, 16, "外来診察初診", dept="外来診察"),
    ])
    found = find_missing_pre_exam(patient, Rules(), TODAY)
    assert len(found) == 1
    assert found[0].exam.menu == "胃＋大腸カメラ検査"
    assert found[0].key == "1:2026-10-31"


def test_find_missing_pre_exam_ignores_past_exams():
    patient = Patient(card_no="1", name="テスト 花子", reservations=[
        r("予約", 2026, 9, 1, "大腸カメラ検査"),
    ])
    assert find_missing_pre_exam(patient, Rules(), TODAY) == []


def test_cancelled_pre_exam_does_not_count():
    patient = Patient(card_no="1", name="テスト 花子", reservations=[
        r("予約", 2026, 10, 31, "大腸カメラ検査"),
        r("患者都合キャンセル", 2026, 10, 20, "大腸カメラ事前診察", dept="外来診察"),
    ])
    assert len(find_missing_pre_exam(patient, Rules(), TODAY)) == 1


def test_select_notifications_filters_kind_and_age():
    rules = Rules(notification_kind="予約確定時", notification_max_age_days=3)
    n = lambda rid, kind, dt: Notification(rid, "テスト 太郎", kind, dt)
    notes = [
        n("1", "予約確定時", datetime(2026, 10, 7, 21, 53)),
        n("1", "Web問診依頼（予約時 及び 予約変更時）", datetime(2026, 10, 7, 21, 53)),
        n("2", "予約確定時", datetime(2026, 10, 1, 9, 0)),   # 古い
        n("3", "予約確定時", None),                           # 日時不明は残す
    ]
    assert [x.reservation_id for x in select_notifications(notes, rules, TODAY)] == ["1", "3"]



def test_find_missing_pre_exam_from_schedule_matches_registered_and_new_patients():
    from precheck.schedule import ScheduleRow
    from precheck.rules import find_missing_pre_exam_from_schedule

    def row(day, hh, menu, name, card_no, birth, status="予約"):
        return ScheduleRow(day=day, start=datetime(day.year, day.month, day.day, hh, 0), status=status, menu=menu,
                           name=name, kana="", card_no=card_no, birth=birth, reservation_no="C1")

    exams = [
        row(date(2026, 10, 31), 13, "胃+大腸カメラ検査", "テスト 太郎", "535", "S49.08.12"),   # 事前診察あり（番号で突合）
        row(date(2026, 10, 31), 14, "連鎖用(胃+大腸)", "テスト 太郎", "535", "S49.08.12"),     # 連鎖用は無視
        row(date(2026, 11, 2), 13, "大腸カメラ検査", "新規 次郎", "", "R01.02.03"),            # 初診・事前診察あり（氏名+生年月日）
        row(date(2026, 11, 5), 13, "大腸カメラ検査", "新規 三郎", "", "H10.05.05"),            # 初診・事前診察なし → 検出
        row(date(2026, 11, 5), 14, "胃カメラ検査", "テスト 花子", "12", "S40.01.01"),          # 大腸ではない
        row(date(2026, 9, 1), 13, "大腸カメラ検査", "過去 四郎", "99", "S30.01.01"),           # 過去 → 無視
    ]
    pres = [
        row(date(2026, 10, 9), 11, "大腸カメラ事前診察", "テスト 太郎", "535", "S49.08.12"),
        row(date(2026, 10, 20), 11, "大腸カメラ事前診察", "新規 次郎", "", "R01.02.03"),
        row(date(2026, 10, 20), 11, "大腸カメラ事前診察", "新規 三郎", "", "H99.99.99"),       # 生年月日が違う別人
    ]
    found = find_missing_pre_exam_from_schedule(exams, pres, Rules(), date(2026, 10, 8))
    assert [(r.name, p.card_no) for r, p, e in found] == [("新規 三郎", "(未登録)")]
