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
    notification_max_age_days: int = 5


@dataclass
class MailConfig:
    enabled: bool = False
    smtp_host: str = ""
    smtp_port: int = 587
    smtp_user: str = ""
    password: str = ""
    from_addr: str = ""
    to_addrs: list[str] = field(default_factory=list)
    subject_template: str = "【要確認】{name}さん 大腸カメラ事前診察の予約なし"


@dataclass
class NotifyConfig:
    output_dir: Path = Path("結果")
    open_after: bool = True       # 結果ファイルをメモ帳で自動的に開く
    toast: bool = True            # Windows の通知も出す
    write_empty: bool = False     # 該当なしでもファイルを作る


@dataclass
class Config:
    wakumy: WakumyConfig
    rules: Rules
    mail: MailConfig
    notify: NotifyConfig
    state_path: Path


def password_file(config_dir: Path) -> Path:
    """パスワードの保存先。local/ は .gitignore 済みで GitHub には上がらない。"""
    return config_dir / "local" / "wakumy_password.txt"


def resolve_password(config_dir: Path, from_config: str = "") -> str:
    """環境変数 → local/wakumy_password.txt → config.toml の順に探す。"""
    env = os.environ.get("WAKUMY_PASSWORD")
    if env:
        return env
    f = password_file(config_dir)
    if f.exists():
        return f.read_text(encoding="utf-8").strip()
    return from_config


def save_password(config_dir: Path, password: str) -> Path:
    f = password_file(config_dir)
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_text(password.strip() + "\n", encoding="utf-8")
    return f


def load(path: str | Path) -> Config:
    path = Path(path)
    with path.open("rb") as f:
        raw = tomllib.load(f)

    w = raw.get("wakumy", {})
    wakumy = WakumyConfig(
        login_url=w["login_url"],
        id=os.environ.get("WAKUMY_ID") or w.get("id", ""),
        password=resolve_password(path.parent, w.get("password", "")),
        headless=bool(w.get("headless", True)),
        timeout_ms=int(w.get("timeout_ms", 20000)),
    )
    if not wakumy.id:
        raise SystemExit("Wakumy の ID が config.toml にありません。")
    if not wakumy.password:
        raise SystemExit(
            "Wakumy のパスワードが保存されていません。setup.bat を実行するか、"
            "コマンドプロンプトで precheck --save-password を実行してください。"
        )

    rules = Rules(**{k: v for k, v in raw.get("rules", {}).items() if k in Rules.__dataclass_fields__})

    m = raw.get("mail", {})
    mail = MailConfig(
        enabled=bool(m.get("enabled", False)),
        smtp_host=m.get("smtp_host", ""),
        smtp_port=int(m.get("smtp_port", 587)),
        smtp_user=m.get("smtp_user", ""),
        password=os.environ.get("SMTP_PASSWORD") or m.get("password", ""),
        from_addr=m.get("from_addr", ""),
        to_addrs=list(m.get("to_addrs", [])),
        subject_template=m.get("subject_template", MailConfig.subject_template),
    )

    n = raw.get("notify", {})
    output_dir = Path(n.get("output_dir", "結果"))
    if not output_dir.is_absolute():
        output_dir = path.parent / output_dir
    notify = NotifyConfig(
        output_dir=output_dir,
        open_after=bool(n.get("open_after", True)),
        toast=bool(n.get("toast", True)),
        write_empty=bool(n.get("write_empty", False)),
    )

    state_path = Path(raw.get("state", {}).get("path", "state/processed.json"))
    if not state_path.is_absolute():
        state_path = path.parent / state_path

    return Config(wakumy=wakumy, rules=rules, mail=mail, notify=notify, state_path=state_path)
