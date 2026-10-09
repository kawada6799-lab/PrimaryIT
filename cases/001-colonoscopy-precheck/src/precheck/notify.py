"""結果の知らせ方。既定はテキストファイルを書いて自動で開く（アカウント不要）。"""
from __future__ import annotations

import os
import subprocess
import sys
from datetime import date
from pathlib import Path

from .models import Finding, Notification

_WEEKDAY = "月火水木金土日"


def build_report(findings: list[Finding], today: date, not_found: list[Notification] | None = None) -> str:
    """看護師がそのまま電話連絡に使える一覧。診察券番号と氏名を先頭に出す。

    not_found: 患者管理で見つからず判定できなかった通知。予約ID を出して手で確認してもらう。
    """
    not_found = not_found or []
    lines = [
        f"大腸カメラ事前診察の予約が無い患者  （{today:%Y/%m/%d} 時点、{len(findings)} 名）",
        "",
        "診察券番号  氏名             検査日時            予約メニュー",
        "-" * 70,
    ]
    for f in sorted(findings, key=lambda f: f.exam.start):
        e = f.exam
        when = f"{e.start:%m/%d}({_WEEKDAY[e.start.weekday()]}) {e.start:%H:%M}"
        name = f.patient.name if f.patient.card_no != "(未登録)" or not f.patient.birth else f"{f.patient.name}（{f.patient.birth}）"
        lines.append(f"{f.patient.card_no:<10}  {name:<14}  {when:<18}  {e.menu}")
    lines += [
        "",
        "Wakumy の患者管理で診察券番号を検索し、事前診察の予約を取ってもらうよう連絡してください。",
    ]
    if not_found:
        lines += [
            "",
            f"■ 患者管理で見つからず、判定できなかった予約（{len(not_found)} 件）。予約IDで手で確認してください。",
            "予約ID      通知に出ていた氏名",
            "-" * 40,
        ]
        for nf in not_found:
            lines.append(f"{nf.reservation_id:<10}  {nf.patient_name}")
    flagged = [f for f in findings if f.check_reasons]
    if flagged:
        lines += [
            "",
            "=" * 70,
            f"※要チェック※（{len(flagged)} 名）  過去1か月に外来受診が無い、または診察券番号が未登録の方",
            "=" * 70,
            "診察券番号  氏名             検査日時            理由",
            "-" * 70,
        ]
        for f in sorted(flagged, key=lambda f: f.exam.start):
            e = f.exam
            when = f"{e.start:%m/%d}({_WEEKDAY[e.start.weekday()]}) {e.start:%H:%M}"
            name = f.patient.name if f.patient.card_no != "(未登録)" or not f.patient.birth else f"{f.patient.name}（{f.patient.birth}）"
            lines.append(f"{f.patient.card_no:<10}  {name:<14}  {when:<18}  {'、'.join(f.check_reasons)}")
    lines += ["", "（precheck が自動作成。AI は使っていません）"]
    return "\n".join(lines) + "\n"


def write_report(text: str, output_dir: Path, today: date) -> Path:
    output_dir.mkdir(parents=True, exist_ok=True)
    path = output_dir / f"大腸カメラ事前診察なし_{today:%Y-%m-%d}.txt"
    # 同じ日に複数回動いたら追記ではなく上書き（最新の一覧だけ残す）
    path.write_text(text, encoding="utf-8-sig")  # メモ帳で文字化けしないよう BOM 付き
    return path


def open_file(path: Path) -> None:
    """Windows なら既定のアプリ（メモ帳）で開く。他の OS では何もしない。"""
    if sys.platform.startswith("win"):
        os.startfile(str(path))  # type: ignore[attr-defined]
    elif sys.platform == "darwin":
        subprocess.Popen(["open", str(path)])


def toast(title: str, message: str) -> None:
    """Windows の通知（トースト）を出す。PowerShell 標準機能だけを使う。失敗しても無視。"""
    if not sys.platform.startswith("win"):
        return
    script = (
        "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null;"
        "$t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);"
        "$n = $t.GetElementsByTagName('text');"
        f"$n.Item(0).AppendChild($t.CreateTextNode('{_ps(title)}')) | Out-Null;"
        f"$n.Item(1).AppendChild($t.CreateTextNode('{_ps(message)}')) | Out-Null;"
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('precheck').Show([Windows.UI.Notifications.ToastNotification]::new($t))"
    )
    try:
        subprocess.run(["powershell", "-NoProfile", "-Command", script], timeout=15, check=False, capture_output=True)
    except Exception:
        pass


def _ps(s: str) -> str:
    return s.replace("'", "''")
