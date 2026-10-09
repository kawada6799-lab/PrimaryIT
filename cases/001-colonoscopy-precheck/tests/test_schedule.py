from datetime import date, datetime

from precheck.schedule import parse_day_view, parse_header_date

DAY = date(2026, 10, 16)

# 内視鏡タブ・日表示の innerText を模したもの（氏名は架空）
SAMPLE = """
2026年10月16日(金)
+ 時間帯枠追加
ステータス
来院時刻(待ち)
予約番号
診察券番号
生年月日(年齢)
患者名
予約メニュー
予約/患者メモ
問診メモ
13:00 - 13:30
0 / 2 枠 （WEB 0/2）
+ 新規予約追加
まだ予約はありません
13:30 - 14:00
1 / 2 枠 （WEB 0/2）
+ 新規予約追加
予約
E8
1234
S50.01.01
(51歳)
テスト タロウ
テスト 太郎 ♂
連鎖(人間ドック)
メンズ
9/24→10/16へ変更 本…
14:30 - 15:00
2 / 2 枠 （WEB 0/2）
+ 新規予約追加
予約
C21
5678
S45.02.02
(56歳)
テスト ハナコ
テスト 花子 ♀
胃＋大腸カメラ検査
鎮静 胃がん、肺がん検…
問診依頼
予約
C22
R01.02.03
(7歳)
シンキ ジロウ
新規 次郎
大腸カメラ検査
15:00 - 15:30
1 / 2 枠 （WEB 0/2）
+ 新規予約追加
予約
C21
5678
S45.02.02
(56歳)
テスト ハナコ
テスト 花子 ♀
連鎖用(胃+大腸)
鎮静 胃がん、肺がん検…
"""


def test_parse_header_date():
    assert parse_header_date("2026年10月16日(金)") == date(2026, 10, 16)


def test_parse_day_view_rows():
    rows = parse_day_view(SAMPLE, DAY)
    assert [r.menu for r in rows] == ["連鎖(人間ドック)", "胃+大腸カメラ検査", "大腸カメラ検査", "連鎖用(胃+大腸)"]  # 全角＋は正規化で半角に
    r0, r1, r2, r3 = rows
    assert r0.start == datetime(2026, 10, 16, 13, 30)
    assert r0.card_no == "1234" and r0.birth == "S50.01.01" and r0.name == "テスト 太郎" and r0.kana == "テスト タロウ"
    assert r0.reservation_no == "E8"
    assert r1.start == datetime(2026, 10, 16, 14, 30) and r1.name == "テスト 花子" and r1.card_no == "5678"
    # 初診で診察券番号が無い患者：氏名＋生年月日がキーになる
    assert r2.card_no == "" and r2.birth == "R01.02.03" and r2.name == "新規 次郎"
    assert r2.patient_key == "nm:新規 次郎|R01.02.03"
    assert r1.patient_key == "no:5678" and r3.patient_key == "no:5678"
    assert all(r.status == "予約" for r in rows)


def test_parse_day_view_web_badge_does_not_shift_name_and_menu():
    text = """
2026年10月10日(土)
10:00 - 10:30
1 / 2 枠 （WEB 1/2）
+ 新規予約追加
予約
WEB
C3
7777
S59.01.01
(42歳)
テスト マサル
テスト 勝 ♂
胃カメラ検査
予約
C4
R01.02.03
(7歳)
シンキ ジロウ
新規 次郎
WEB
大腸カメラ検査
"""
    rows = parse_day_view(text, date(2026, 10, 10))
    assert [(r.name, r.menu, r.card_no) for r in rows] == [
        ("テスト 勝", "胃カメラ検査", "7777"),
        ("新規 次郎", "大腸カメラ検査", ""),
    ]
