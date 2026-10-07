"""config.toml と環境変数から設定を読む。"""
from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class WakumyConfig:
    login_url: str
    id: str
    password: str
    headless: bool = True
    timeout_ms: int = 20000


@dataclass
class Rules:
    exam_keywords: list[str] = field(default_factory=lambda: ["大腸"])
    exam_exclude_keywords: list[str] = field(default_factory=lambda: ["連鎖用", "事前診察"])
    pre_exam_keywords: list[str] = field(default_factory=lambda: ["大腸カメラ事前診察"])
    window_days: int = 30
    exam_active_statuses: list[str] = field(default_factory=lambda: ["予約"])
    pre_exam_ok_statuses: list[str] = field(default_factory=lambda: ["予約", "来院"])
    notification_kind: str = "予約確定時"
    notification_max_age_days: int = 3


@dataclass
class MailConfig:
    enabled: bool = True
    smtp_host: str = ""
    smtp_port: int = 587
    smtp_user: str = ""
    password: str = ""
    from_addr: str = ""
    to_addrs: list[str] = field(default_factory=list)
    subject_template: str = "【要確認】{name}さん 大腸カメラ事前診察の予約なし"


@dataclass
class Config:
    wakumy: WakumyConfig
    rules: Rules
    mail: MailConfig
    state_path: Path


def load(path: str | Path) -> Config:
    path = Path(path)
    with path.open("rb") as f:
        raw = tomllib.load(f)

    w = raw.get("wakumy", {})
    wakumy = WakumyConfig(
        login_url=w["login_url"],
        id=os.environ.get("WAKUMY_ID") or w.get("id", ""),
        password=os.environ.get("WAKUMY_PASSWORD") or w.get("password", ""),
        headless=bool(w.get("headless", True)),
        timeout_ms=int(w.get("timeout_ms", 20000)),
    )
    if not wakumy.id or not wakumy.password:
        raise SystemExit("Wakumy の ID/パスワードがありません。環境変数 WAKUMY_ID / WAKUMY_PASSWORD を設定してください。")

    rules = Rules(**{k: v for k, v in raw.get("rules", {}).items() if k in Rules.__dataclass_fields__})

    m = raw.get("mail", {})
    mail = MailConfig(
        enabled=bool(m.get("enabled", True)),
        smtp_host=m.get("smtp_host", ""),
        smtp_port=int(m.get("smtp_port", 587)),
        smtp_user=m.get("smtp_user", ""),
        password=os.environ.get("SMTP_PASSWORD") or m.get("password", ""),
        from_addr=m.get("from_addr", ""),
        to_addrs=list(m.get("to_addrs", [])),
        subject_template=m.get("subject_template", MailConfig.subject_template),
    )

    state_path = Path(raw.get("state", {}).get("path", "state/processed.json"))
    if not state_path.is_absolute():
        state_path = path.parent / state_path

    return Config(wakumy=wakumy, rules=rules, mail=mail, state_path=state_path)
