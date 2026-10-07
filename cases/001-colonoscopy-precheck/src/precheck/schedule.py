"""予約一覧（日表示）の画面文字列を予約の一覧に変換する純粋関数。

画面（内視鏡タブ・日表示）は <table> ではなく、時間枠ごとに
  「13:30 - 14:00  1/2枠 (WEB 0/2)  + 新規予約追加」
という見出し行があり、その下に予約の行が並ぶ。1行の内容は innerText 上では
  予約 / E8 / 1234 / S50.01.01 / (51歳) / テスト タロウ / テスト 太郎 / 連鎖(人間ドック) / メモ…
の順に改行区切りで現れる（実機で確認して調整する前提）。
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import date, datetime

from .parse import normalize, normalize_name

_SLOT = re.compile(r"^(\d{1,2}):(\d{2})\s*[-～〜]\s*(\d{1,2}):(\d{2})")
_STATUS = re.compile(r"^(予約|仮予約|来院|受付|受付済|診察中|会計|終了|患者都合キャンセル|医院都合キャンセル|キャンセル|無断キャンセル)$")
_CARD_NO = re.compile(r"^\d{1,8}$")
_BIRTH = re.compile(r"^[MTSHR]\d{1,2}\.\d{1,2}\.\d{1,2}$")
_AGE = re.compile(r"^[（(]\d{1,3}歳[)）]$")
_KANA = re.compile(r"^[゠-ヿｦ-ﾟー\s・]+$")  # 全角/半角カタカナのみ
_NOISE = re.compile(r"^(\+\s*新規予約追加|まだ予約はありません|\d+\s*/\s*\d+\s*枠.*|[（(]WEB.*|問診依頼|メンズ|レディース|♂|♀)$")
_HEADER_DATE = re.compile(r"(\d{4})年(\d{1,2})月(\d{1,2})日")


@dataclass(frozen=True)
class ScheduleRow:
    """予約一覧の1行。"""

    day: date
    start: datetime
    status: str
    menu: str
    name: str            # 漢字氏名（取れなければカナ）
    kana: str
    card_no: str         # 診察券番号。初診で未登録なら空
    birth: str           # 生年月日（和暦のまま、例 S50.01.01）
    reservation_no: str  # 予約番号（例 E8）

    @property
    def patient_key(self) -> str:
        """同一患者とみなすキー。診察券番号があれば番号、無ければ氏名＋生年月日。"""
        if self.card_no:
            return f"no:{self.card_no}"
        return f"nm:{normalize_name(self.name)}|{self.birth}"


def parse_header_date(text: str) -> date | None:
    m = _HEADER_DATE.search(normalize(text))
    if not m:
        return None
    return date(int(m[1]), int(m[2]), int(m[3]))


def parse_day_view(text: str, day: date) -> list[ScheduleRow]:
    """日表示画面の innerText から予約行を取り出す。"""
    lines = [normalize(l) for l in text.splitlines()]
    lines = [l for l in lines if l]
    rows: list[ScheduleRow] = []
    slot_start: datetime | None = None
    i = 0
    while i < len(lines):
        line = lines[i]
        m = _SLOT.match(line)
        if m:
            slot_start = datetime(day.year, day.month, day.day, int(m[1]), int(m[2]))
            i += 1
            continue
        if _STATUS.match(line) and slot_start is not None:
            # ステータス行から次のステータス行／時間枠行の手前までが1件
            j = i + 1
            body: list[str] = []
            while j < len(lines) and not _STATUS.match(lines[j]) and not _SLOT.match(lines[j]):
                body.append(lines[j])
                j += 1
            row = _row_from_body(line, body, slot_start, day)
            if row:
                rows.append(row)
            i = j
            continue
        i += 1
    return rows


def _row_from_body(status: str, body: list[str], start: datetime, day: date) -> ScheduleRow | None:
    body = [b for b in body if not _NOISE.match(b)]
    reservation_no = card_no = birth = kana = name = menu = ""
    rest: list[str] = []
    for b in body:
        if not birth and _BIRTH.match(b):
            birth = b
        elif _AGE.match(b):
            continue
        elif not card_no and _CARD_NO.match(b) and not reservation_no:
            # 予約番号（E8 など）より先に数字だけの行が来ることは無い想定。数字だけなら診察券番号
            card_no = b
        elif not card_no and _CARD_NO.match(b):
            card_no = b
        elif not reservation_no and re.match(r"^[A-Za-z]{1,3}\d{1,4}$", b):
            reservation_no = b
        elif not kana and _KANA.match(b) and len(b) >= 2:
            kana = b
        else:
            rest.append(b)
    # 残りの先頭が氏名（カナの直後に漢字氏名が来る）、その次がメニュー
    rest = [re.sub(r"\s*[♂♀]\s*$", "", r) for r in rest]
    if rest:
        name = rest[0]
        rest = rest[1:]
    if rest:
        menu = rest[0]
    if not name and kana:
        name = kana
    if not menu and not name:
        return None
    return ScheduleRow(
        day=day, start=start, status=status, menu=menu, name=normalize_name(name), kana=kana,
        card_no=card_no, birth=birth, reservation_no=reservation_no,
    )
