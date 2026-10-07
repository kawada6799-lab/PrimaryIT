"""Wakumy 画面の文字列を解釈する純粋関数。"""
from __future__ import annotations

import re
import unicodedata
from datetime import datetime

# 例: "2026/10/31 (土) 14:00 ～ 14:30"
_RESERVATION_DT = re.compile(
    r"(?P<y>\d{4})/(?P<m>\d{1,2})/(?P<d>\d{1,2})\s*(?:\([^)]*\))?\s*(?P<hh>\d{1,2}):(?P<mm>\d{2})"
)
# 例: "2026/10/07 21:53:23"
_DATETIME = re.compile(
    r"(?P<y>\d{4})/(?P<m>\d{1,2})/(?P<d>\d{1,2})\s+(?P<hh>\d{1,2}):(?P<mm>\d{2})(?::(?P<ss>\d{2}))?"
)
# 例: "15421790 (山田 太郎)"
_NOTIFICATION_RESERVATION = re.compile(r"^\s*(?P<id>\d+)\s*[（(]\s*(?P<name>.+?)\s*[)）]\s*$")


def parse_reservation_start(text: str) -> datetime | None:
    """予約日時セルの文字列から開始日時を取り出す。"""
    m = _RESERVATION_DT.search(normalize(text))
    if not m:
        return None
    return datetime(int(m["y"]), int(m["m"]), int(m["d"]), int(m["hh"]), int(m["mm"]))


def parse_datetime(text: str) -> datetime | None:
    """通知完了日時などの "YYYY/MM/DD HH:MM[:SS]" を解釈する。"""
    m = _DATETIME.search(normalize(text))
    if not m:
        return None
    return datetime(
        int(m["y"]), int(m["m"]), int(m["d"]),
        int(m["hh"]), int(m["mm"]), int(m["ss"] or 0),
    )


def parse_notification_reservation(text: str) -> tuple[str, str] | None:
    """予約通知一覧の「予約」セル "12345 (姓 名)" を (予約ID, 患者名) に分ける。"""
    m = _NOTIFICATION_RESERVATION.match(normalize(text))
    if not m:
        return None
    return m["id"], normalize_name(m["name"])


def normalize(text: str) -> str:
    """全角英数・空白を半角に寄せ、前後の空白を落とす。"""
    return unicodedata.normalize("NFKC", text or "").strip()


def normalize_name(text: str) -> str:
    """氏名の比較用。全角/半角スペースや連続スペースの違いを吸収する。"""
    return " ".join(normalize(text).split())


def strip_header(text: str) -> str:
    """表のヘッダ文字列から並べ替えアイコンなどの余計な記号を落とす。"""
    t = normalize(text)
    # ヘッダに混ざる記号類（⇅ ▲ ▼ ? など）と改行以降の補足行を落とす
    t = t.splitlines()[0] if t else t
    return re.sub(r"[^\w　-ヿ一-鿿（）()/・＋+]", "", t)
