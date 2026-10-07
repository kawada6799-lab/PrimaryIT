"""Playwright で Wakumy 医療機関管理画面を操作する。

先生から聞き取った手順①〜⑨をそのまま自動化している。
画面の部品は「表示されている文字」で探す（class 名は Vue のビルドで変わるため）。
※ 実機（先生のPC）での初回実行時にセレクタの調整が必要になる可能性がある。
"""
from __future__ import annotations

import logging
import re
from datetime import date
from pathlib import Path
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


log = logging.getLogger("precheck")


class WakumyError(RuntimeError):
    pass


class Wakumy:
    def __init__(self, cfg: WakumyConfig):
        self.cfg = cfg
        self._pw = None
        self._browser = None
        self.page: Page | None = None
        self.debug_dir: Path | None = None  # 失敗時の画面保存先

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

    def read_notifications(self, oldest: date | None = None, max_pages: int = 50, kind: str | None = None) -> list[Notification]:
        """予約通知一覧を新しい順に読む。

        kind を渡すと、先に「詳細検索 → 通知種別 → 検索」で画面側を絞り込む（件数が大幅に減る）。

        oldest より古い通知完了日時の行が出てきたらそこで打ち切る（一覧は新しい順という前提）。
        4日に1回の実行だと1ページに収まらないので、ページ送りして読む。
        """
        self.open_notifications()
        p = self.page
        p.wait_for_load_state("networkidle")
        if kind:
            self._filter_notifications_by_kind(kind)
        table = _table_with_header(p, "通知種別")
        _wait_for_rows(table, self.cfg.timeout_ms)
        out: list[Notification] = []
        for _ in range(max_pages):
            for row in _read_table(table):
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
                if oldest and out and out[-1].completed_at and out[-1].completed_at.date() >= oldest:
                    log.warning(
                        "予約通知一覧を %d 件読みましたが、まだ %s より新しい通知が続いている可能性があります"
                        "（ページ送りが見つからず、最後の通知は %s）。表示件数の上限で切れているなら実行間隔を短くしてください。",
                        len(out), oldest, out[-1].completed_at,
                    )
                break
            _wait_for_rows(table, self.cfg.timeout_ms)
        return out

    def _filter_notifications_by_kind(self, kind: str) -> bool:
        """「詳細検索」を開き、通知種別のプルダウンで kind を選んで「検索」を押す。

        どの段階で止まったかをログに出し、失敗時は画面を debug_dir に保存する。
        失敗しても致命的ではない（コード側でも通知種別で絞るため）。
        """
        p = self.page
        step = "「詳細検索」ボタン"
        try:
            p.get_by_text("詳細検索", exact=True).first.click(timeout=5000)
            log.info("絞り込み 1/4: 「詳細検索」を押した")

            step = "「通知種別」のプルダウン"
            label = p.locator("label").filter(has_text=re.compile(r"^\s*通知種別\s*$")).first
            if label.count() == 0:
                label = p.get_by_text("通知種別", exact=True).first
            label.wait_for(timeout=5000)
            box = label.locator(
                "xpath=following::*[self::select or self::input or @role='combobox' "
                "or contains(@class,'multiselect') or contains(@class,'select')][1]"
            ).first
            box.wait_for(timeout=5000)
            tag = (box.evaluate("e => e.tagName") or "").lower()
            if tag == "select":
                box.select_option(label=kind)
            else:
                box.click(timeout=5000)
                option = p.get_by_role("option", name=kind, exact=True)
                if option.count() == 0:
                    option = p.locator("li:visible, [role='option']:visible").filter(
                        has_text=re.compile(rf"^\s*{re.escape(kind)}\s*$")
                    )
                if option.count() == 0:
                    # 最後の手段: 入力して Enter（vue-select / vue-multiselect はこれで選べる）
                    p.keyboard.type(kind)
                    p.keyboard.press("Enter")
                else:
                    option.first.click(timeout=5000)
            log.info("絞り込み 2/4: 通知種別「%s」を選んだ", kind)

            step = "「検索」ボタン"
            search = p.locator("button:has-text('検索'):not(:has-text('詳細'))")
            if search.count() == 0:
                search = p.get_by_role("button", name=re.compile(r"^\s*検索\s*$"))
            if search.count() == 0:
                search = p.get_by_text("検索", exact=True)
            search.first.click(timeout=5000)
            p.wait_for_load_state("networkidle")
            log.info("絞り込み 3/4: 「検索」を押した")

            step = "絞り込み結果の確認"
            table = _table_with_header(p, "通知種別")
            _wait_for_rows(table, self.cfg.timeout_ms)
            rows = _read_table(table)
            kinds = {normalize(r.get("通知種別", "")) for r in rows}
            if rows and kinds <= {normalize(kind)}:
                log.info("絞り込み 4/4: 一覧が「%s」だけになった（%d 行）", kind, len(rows))
                return True
            log.warning("絞り込み 4/4: 「検索」後も他の通知種別が混ざっています: %s", sorted(kinds)[:5])
            self._debug_shot("filter_not_applied")
            return False
        except PwTimeout:
            log.warning("通知種別での絞り込みに失敗: %s で止まりました。絞り込まずに読みます。", step)
            self._debug_shot("filter_failed")
            return False

    # ---------- 予約一覧（日表示）を日付ごとに読む ----------
    def open_schedule_tab(self, tab: str) -> None:
        """ヘッダーの「予約一覧」→ 診療科タブ（内視鏡 / 外来診察 …）→ 日表示。"""
        p = self.page
        self._header_nav("予約一覧").click()
        p.get_by_text("時間帯枠追加").first.wait_for()
        p.get_by_text(tab, exact=True).first.click()
        p.wait_for_load_state("networkidle")
        day_btn = p.get_by_text("日表示", exact=True)
        if day_btn.count():
            day_btn.first.click()
            p.wait_for_load_state("networkidle")

    def goto_day(self, d: date) -> None:
        """日表示の日付を d に合わせる。まず「N 日後」入力、だめなら左のカレンダーで移動する。"""
        p = self.page
        if self._schedule_header_date() == d:
            return
        # 1) 「0 日後」入力欄：今日からの日数を入れて Enter
        inp = p.get_by_text("日後", exact=True).first.locator("xpath=preceding::input[1]")
        if inp.count():
            try:
                inp.fill(str((d - date.today()).days))
                inp.press("Enter")
                p.wait_for_load_state("networkidle")
                if self._schedule_header_date() == d:
                    return
            except PwTimeout:
                pass
        # 2) カレンダー：月を合わせてから日をクリック
        cal = p.get_by_text("次の月", exact=True).first.locator("xpath=ancestor::*[contains(., '前の月')][1]")
        for _ in range(24):
            shown = self._calendar_month(cal)
            if shown is None or shown == (d.year, d.month):
                break
            cal.get_by_text("次の月" if (d.year, d.month) > shown else "前の月", exact=True).first.click()
            p.wait_for_timeout(300)
        cells = cal.get_by_text(f"{d.day:02d}", exact=True).or_(cal.get_by_text(str(d.day), exact=True))
        if cells.count() == 0:
            raise WakumyError(f"カレンダーに {d} が見つかりません")
        # 前月末・翌月初の薄い日付と重複することがある。月前半は最初、月後半は最後の一致を選ぶ
        (cells.first if d.day < 15 else cells.last).click()
        p.wait_for_load_state("networkidle")
        if self._schedule_header_date() != d:
            raise WakumyError(f"日付を {d} に移動できませんでした（表示は {self._schedule_header_date()}）")

    def _schedule_header_date(self) -> date | None:
        from .schedule import parse_header_date
        heads = self.page.locator("text=/\\d{4}年\\d{1,2}月\\d{1,2}日/")
        if heads.count() == 0:
            return None
        return parse_header_date(heads.first.inner_text())

    def _calendar_month(self, cal: Locator) -> tuple[int, int] | None:
        m = re.search(r"(\d{2,4})年\s*(\d{1,2})月", normalize(cal.inner_text()))
        if not m:
            return None
        y = int(m[1])
        if y < 100:
            y += 2000
        return (y, int(m[2]))

    def read_day_view(self, d: date, tab: str = "") -> list:
        """表示中の日表示を読み、予約行のリストにする。"""
        from .schedule import parse_day_view
        p = self.page
        p.wait_for_load_state("networkidle")
        area = p.get_by_text("時間帯枠追加").first.locator("xpath=ancestor::*[contains(., '予約メニュー')][1]")
        text = area.inner_text() if area.count() else p.locator("body").inner_text()
        rows = parse_day_view(text, d)
        if self.debug_dir and (not rows) and ("まだ予約はありません" not in text):
            self._debug_text(f"day_{tab}_{d.isoformat()}", text)
        return rows

    def _debug_text(self, name: str, text: str) -> None:
        if not self.debug_dir:
            return
        try:
            self.debug_dir.mkdir(parents=True, exist_ok=True)
            path = self.debug_dir / f"debug_{name}.txt"
            path.write_text(text, encoding="utf-8")
            log.info("画面の文字を保存しました: %s（患者情報を含むので取り扱い注意）", path)
        except Exception:
            pass

    def _debug_shot(self, name: str) -> None:
        """調査用に画面を保存する（debug_dir が設定されているときだけ）。"""
        if not self.debug_dir:
            return
        try:
            self.debug_dir.mkdir(parents=True, exist_ok=True)
            path = self.debug_dir / f"debug_{name}.png"
            self.page.screenshot(path=str(path), full_page=True)
            log.info("画面を保存しました: %s（患者情報が映るので取り扱い注意）", path)
        except Exception:  # 保存できなくても本処理は続ける
            pass

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
        table = _table_with_header(p, "診察券番号")
        rows = self._wait_for_search_result(table, name)
        hits = _match_patient_rows(rows, name)
        if not hits:
            log.debug("検索「%s」の結果 %d 行: %s", name, len(rows),
                      "; ".join(f"{r.get('診察券番号','')}:{r.get('姓','')} {r.get('名','')}({r.get('セイ','')} {r.get('メイ','')})" for r in rows[:10]))
        return hits

    def _wait_for_search_result(self, table: Locator, name: str, timeout_ms: int = 6000) -> list[dict[str, str]]:
        """検索結果が検索語を反映するまで待つ。絞り込み前の全患者一覧を読んでしまうのを防ぐ。"""
        import time
        tokens = [t for t in normalize_name(name).split(" ") if t]
        deadline = time.monotonic() + timeout_ms / 1000
        rows: list[dict[str, str]] = []
        while True:
            rows = _read_table(table)
            if rows and all(any(tok in " ".join(r.values()) for tok in tokens) for r in rows):
                return rows
            if time.monotonic() > deadline:
                return rows
            self.page.wait_for_timeout(300)

    def open_patient(self, card_no: str, fallback_name: str = "") -> Patient:
        """検索結果の中から診察券番号が一致する行をクリックして患者ページを開き、予約一覧を読む。"""
        p = self.page
        table = _table_with_header(p, "診察券番号")
        # has= に渡すロケータは page 起点で書く（行の内側から探される）。table 起点だと一致しない
        card_cell = p.locator("td", has_text=re.compile(rf"^\s*{re.escape(card_no)}\s*$"))
        row = table.locator("tbody tr").filter(has=card_cell).first
        try:
            row.wait_for()
        except PwTimeout as e:
            raise WakumyError(f"検索結果に診察券番号 {card_no} の行が見つかりませんでした。") from e
        row.locator("td").nth(1).click()  # 「姓」セル。右端の「この患者で予約を取得」ボタンは避ける
        p.get_by_text("患者情報").first.wait_for()
        p.get_by_text("予約一覧").first.wait_for()
        name = _side_value(p, "氏名") or fallback_name
        card = _side_value(p, "診察券番号") or card_no
        return Patient(card_no=card, name=normalize_name(name), reservations=list(self._read_reservations()))

    # ---------- ⑧ 患者ページの予約一覧 ----------
    def _read_reservations(self) -> Iterator[Reservation]:
        p = self.page
        p.wait_for_load_state("networkidle")
        while True:
            table = _table_with_header(p, "予約メニュー")
            _wait_for_rows(table, 5000)  # 予約が 0 件の患者もいる
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



def _match_patient_rows(rows: list[dict[str, str]], name: str) -> list[dict[str, str]]:
    """通知に出た氏名と患者一覧の行を照合する。

    1. 姓+名 が一致（スペースの有無・全角半角の違いは無視）
    2. セイ+メイ（カナ）が一致。通知がカナ氏名のことがある
    3. どちらも無く、検索結果が 1 行だけならその行（Wakumy 側で既に絞り込まれている）
    """
    def squash(t: str) -> str:
        return normalize_name(t).replace(" ", "")

    target = squash(name)
    if not target:
        return []
    by_kanji = [r for r in rows if squash(f"{r.get('姓','')}{r.get('名','')}") == target]
    if by_kanji:
        return by_kanji
    by_kana = [r for r in rows if squash(f"{r.get('セイ','')}{r.get('メイ','')}") == target]
    if by_kana:
        return by_kana
    if len(rows) == 1:
        return rows
    return []


def _table_with_header(page: Page, header_text: str) -> Locator:
    """ヘッダ（th）にその文字を含む <table> を返す。画面に表が複数あっても取り違えない。"""
    return page.locator("table").filter(has=page.locator("th", has_text=header_text)).first


def _wait_for_rows(table: Locator, timeout_ms: int) -> bool:
    """表に行（tbody tr）が現れるまで待つ。SPA はヘッダだけ先に出て中身が後から届く。"""
    try:
        table.locator("tbody tr").first.wait_for(timeout=timeout_ms)
        return True
    except PwTimeout:
        return False


def _go_next_page(page: Page) -> bool:
    """ページ送りの「次へ」を押す。無い／押せないなら False。

    BootstrapVue の b-pagination は <button role="menuitem" aria-label="Go to next page"> を出し、
    最終ページでは button ではなく <span aria-disabled="true"> になる。
    """
    nxt = page.locator(
        "button[aria-label*='next' i], a[aria-label*='next' i], "
        "button[aria-label*='次'], a[aria-label*='次']"
    )
    if nxt.count() == 0:
        return False
    btn = nxt.first
    if btn.is_disabled() or btn.get_attribute("aria-disabled") == "true" or btn.get_attribute("tabindex") == "-1":
        return False
    li = btn.locator("xpath=ancestor::li[1]")
    if li.count() and "disabled" in (li.first.get_attribute("class") or ""):
        return False  # 最終ページ。<li class="disabled"> がクリックを遮るので押さない
    btn.click()
    page.wait_for_load_state("networkidle")
    return True


def _side_value(page: Page, label: str) -> str:
    """患者情報パネルの「ラベル → 値」を取る。ラベルの次の要素が値、という構造を想定。"""
    lab = page.get_by_text(label, exact=True).first
    try:
        lab.wait_for(timeout=3000)
        sib = lab.locator("xpath=following-sibling::*[1]")
        if sib.count():
            return normalize(sib.first.inner_text(timeout=3000))
        # ラベルと値が同じ要素に入っているパターン（例: "診察券番号\n535"）
        text = normalize(lab.inner_text(timeout=3000))
        return text.replace(label, "", 1).strip()
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
