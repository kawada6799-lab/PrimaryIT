"""Playwright で Wakumy 医療機関管理画面を操作する。

先生から聞き取った手順①〜⑨をそのまま自動化している。
画面の部品は「表示されている文字」で探す（class 名は Vue のビルドで変わるため）。
※ 実機（先生のPC）での初回実行時にセレクタの調整が必要になる可能性がある。
"""
from __future__ import annotations

import re
from datetime import date
from typing import Iterator

from playwright.sync_api import Locator, Page, TimeoutError as PwTimeout, sync_playwright

from .config import WakumyConfig
from .models import Notification, Patient, Reservation
from .parse import (
    normalize,
    normalize_name,
    parse_datetime,
    parse_notification_reservation,
    parse_reservation_start,
    strip_header,
)


class WakumyError(RuntimeError):
    pass


class Wakumy:
    def __init__(self, cfg: WakumyConfig):
        self.cfg = cfg
        self._pw = None
        self._browser = None
        self.page: Page | None = None

    # ---------- ライフサイクル ----------
    def __enter__(self) -> "Wakumy":
        self._pw = sync_playwright().start()
        self._browser = self._pw.chromium.launch(headless=self.cfg.headless)
        context = self._browser.new_context(locale="ja-JP", viewport={"width": 1400, "height": 900})
        context.set_default_timeout(self.cfg.timeout_ms)
        self.page = context.new_page()
        return self

    def __exit__(self, *exc) -> None:
        if self._browser:
            self._browser.close()
        if self._pw:
            self._pw.stop()

    # ---------- ① ログイン ----------
    def login(self) -> None:
        p = self.page
        p.goto(self.cfg.login_url)
        p.get_by_text("医療機関ログイン").wait_for()
        # ラベル "ID:" と "パスワード:" の右の入力欄
        p.locator("input:not([type='password'])").first.fill(self.cfg.id)
        p.locator("input[type='password']").first.fill(self.cfg.password)
        p.get_by_role("button", name="ログイン").click()
        try:
            self._header_nav("予約一覧").wait_for()
        except PwTimeout as e:
            raise WakumyError("ログイン後の画面が開きませんでした。ID/パスワードを確認してください。") from e

    def _header_nav(self, label: str) -> Locator:
        return self.page.get_by_role("link", name=label).or_(self.page.get_by_role("button", name=label)).first

    # ---------- ②③④ 予約通知一覧 ----------
    def open_notifications(self) -> None:
        p = self.page
        # ヘッダー右端の「≡▾」ドロップダウン。中に「予約通知」「患者画面（院外）」「ログアウト」がある
        menu = p.get_by_role("menuitem", name="予約通知")
        if not menu.is_visible():
            p.locator("header, .hospital-app-header-menu, nav").locator("button.dropdown-toggle").last.click()
            menu.wait_for()
        menu.click()
        p.get_by_text("予約通知一覧").wait_for()

    def read_notifications(self, oldest: date | None = None, max_pages: int = 50) -> list[Notification]:
        """予約通知一覧を新しい順に読む。

        oldest より古い通知完了日時の行が出てきたらそこで打ち切る（一覧は新しい順という前提）。
        4日に1回の実行だと1ページに収まらないので、ページ送りして読む。
        """
        self.open_notifications()
        p = self.page
        out: list[Notification] = []
        for _ in range(max_pages):
            for row in _read_table(self._main_table()):
                parsed = parse_notification_reservation(row.get("予約", ""))
                if not parsed:
                    continue
                rid, name = parsed
                n = Notification(
                    reservation_id=rid,
                    patient_name=name,
                    kind=normalize(row.get("通知種別", "")),
                    completed_at=parse_datetime(row.get("通知完了日時", "")),
                )
                if oldest and n.completed_at and n.completed_at.date() < oldest:
                    return out
                out.append(n)
            if not _go_next_page(p):
                break
        return out

    # ---------- ⑤⑥⑦ 患者管理で検索して患者ページへ ----------
    def search_patients(self, name: str) -> list[dict[str, str]]:
        p = self.page
        self._header_nav("患者管理").click()
        p.get_by_text("患者一覧").wait_for()
        box = p.locator("input[type='text'], input[type='search']").first
        box.fill("")
        box.fill(name)
        box.press("Enter")
        p.wait_for_load_state("networkidle")
        rows = _read_table(self._main_table())
        target = normalize_name(name)
        return [r for r in rows if normalize_name(f"{r.get('姓','')} {r.get('名','')}") == target]

    def open_patient(self, card_no: str) -> Patient:
        """検索結果の中から診察券番号が一致する行をクリックして患者ページを開き、予約一覧を読む。"""
        p = self.page
        table = self._main_table()
        row = table.locator("tbody tr").filter(has=table.locator("td").filter(has_text=re.compile(rf"^\s*{re.escape(card_no)}\s*$"))).first
        row.locator("td").nth(1).click()  # 「姓」セル。右端の「この患者で予約を取得」ボタンは避ける
        p.get_by_text("患者情報").wait_for()
        p.get_by_text("予約一覧").wait_for()
        name = _side_value(p, "氏名")
        card = _side_value(p, "診察券番号") or card_no
        return Patient(card_no=card, name=normalize_name(name), reservations=list(self._read_reservations()))

    # ---------- ⑧ 患者ページの予約一覧 ----------
    def _read_reservations(self) -> Iterator[Reservation]:
        p = self.page
        while True:
            table = p.locator("table").filter(has=p.locator("th", has_text="予約メニュー")).first
            for row in _read_table(table):
                start = parse_reservation_start(row.get("予約日時", ""))
                if start is None:
                    continue
                yield Reservation(
                    status=normalize(row.get("ステータス", "")),
                    start=start,
                    department=normalize(row.get("診療科目", "")),
                    menu=normalize(row.get("予約メニュー", "")),
                    memo=normalize(row.get("予約メモ", "")),
                )
            if not _go_next_page(p):
                break

    # ---------- 共通 ----------
    def _main_table(self) -> Locator:
        return self.page.locator("table").first


def _go_next_page(page: Page) -> bool:
    """BootstrapVue のページ送り「次へ」を押す。無い／押せないなら False。"""
    nxt = page.get_by_role("button", name=re.compile("next page|次へ|次のページ", re.I))
    if nxt.count() == 0 or nxt.first.is_disabled():
        return False
    nxt.first.click()
    page.wait_for_load_state("networkidle")
    return True


def _side_value(page: Page, label: str) -> str:
    """患者情報パネルの「ラベル → 値」を取る。ラベルの次の要素が値、という構造を想定。"""
    lab = page.get_by_text(label, exact=True).first
    try:
        return normalize(lab.locator("xpath=following-sibling::*[1]").inner_text())
    except PwTimeout:
        return ""


def _read_table(table: Locator) -> list[dict[str, str]]:
    """<table> をヘッダ名 → セル文字列の dict のリストにする。"""
    headers = [strip_header(h) for h in table.locator("thead th").all_inner_texts()]
    out: list[dict[str, str]] = []
    for tr in table.locator("tbody tr").all():
        cells = tr.locator("td").all_inner_texts()
        if not cells or all(not c.strip() for c in cells):
            continue
        out.append({h: normalize(c) for h, c in zip(headers, cells) if h})
    return out
