"""結果の知らせ方。既定はテキストファイルを書いて自動で開く（アカウント不要）。"""
from __future__ import annotations

import os
import subprocess
import sys
from datetime import date
from pathlib import Path

from .models import Finding

_WEEKDAY = "月火水木金土日"


def build_report(findings: list[Finding], today: date) -> str:
    """看護師がそのまま電話連絡に使える一覧。診察券番号と氏名を先頭に出す。"""
    lines = [
        f"大腸カメラ事前診察の予約が無い患者  （{today:%Y/%m/%d} 時点、{len(findings)} 名）",
        "",
        "診察券番号  氏名             検査日時            予約メニュー",
        "-" * 70,
    ]
    for f in sorted(findings, key=lambda f: f.exam.start):
        e = f.exam
        when = f"{e.start:%m/%d}({_WEEKDAY[e.start.weekday()]}) {e.start:%H:%M}"
        lines.append(f"{f.patient.card_no:<10}  {f.patient.name:<14}  {when:<18}  {e.menu}")
    lines += [
        "",
        "Wakumy の患者管理で診察券番号を検索し、事前診察の予約を取ってもらうよう連絡してください。",
        "（precheck が自動作成。AI は使っていません）",
    ]
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
