from datetime import datetime

from precheck.parse import (
    normalize_name,
    parse_datetime,
    parse_notification_reservation,
    parse_reservation_start,
    strip_header,
)


def test_parse_reservation_start_with_weekday_and_range():
    assert parse_reservation_start("2026/10/31 (土) 14:00 ～ 14:30") == datetime(2026, 10, 31, 14, 0)


def test_parse_reservation_start_fullwidth():
    assert parse_reservation_start("２０２６/１０/０９（金）１１:００～１１:３０") == datetime(2026, 10, 9, 11, 0)


def test_parse_reservation_start_invalid():
    assert parse_reservation_start("") is None
    assert parse_reservation_start("予約日時") is None


def test_parse_datetime():
    assert parse_datetime("2026/10/07 21:53:23") == datetime(2026, 10, 7, 21, 53, 23)
    assert parse_datetime("即時") is None


def test_parse_notification_reservation():
    assert parse_notification_reservation("15421790 (山田 太郎)") == ("15421790", "山田 太郎")
    assert parse_notification_reservation("15421790（山田　太郎）") == ("15421790", "山田 太郎")
    assert parse_notification_reservation("予約") is None


def test_normalize_name_spaces():
    assert normalize_name("山田　 太郎 ") == "山田 太郎"


def test_strip_header_drops_icons_and_subtitle():
    assert strip_header("予約 ⇅") == "予約"
    assert strip_header("診察券番号\n生年月日(年齢)") == "診察券番号"
    assert strip_header("ステータス\n来院時刻(待ち)") == "ステータス"


def test_match_patient_rows():
    from precheck.wakumy import _match_patient_rows  # noqa: E402  (playwright は import されるだけ)

    rows = [
        {"診察券番号": "1", "姓": "山田", "名": "太郎", "セイ": "ヤマダ", "メイ": "タロウ"},
        {"診察券番号": "2", "姓": "山田", "名": "花子", "セイ": "ヤマダ", "メイ": "ハナコ"},
    ]
    assert _match_patient_rows(rows, "山田 太郎")[0]["診察券番号"] == "1"
    assert _match_patient_rows(rows, "山田　太郎")[0]["診察券番号"] == "1"      # 全角スペース
    assert _match_patient_rows(rows, "ヤマダ ハナコ")[0]["診察券番号"] == "2"    # カナで通知された場合
    assert _match_patient_rows(rows, "ﾔﾏﾀﾞ ﾊﾅｺ")[0]["診察券番号"] == "2"        # 半角カナ
    assert _match_patient_rows(rows, "佐藤 一郎") == []
    # 検索結果が 1 行だけなら表記が違ってもその行
    assert _match_patient_rows(rows[:1], "山田 太朗")[0]["診察券番号"] == "1"
