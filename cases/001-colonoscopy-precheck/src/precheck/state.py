"""処理済みの予約ID・通知済みの検査を記録する小さな JSON ファイル。"""
from __future__ import annotations

import json
from datetime import datetime
from pathlib import Path


class State:
    def __init__(self, path: Path):
        self.path = path
        self._data: dict = {"processed_notifications": {}, "notified_findings": {}}
        if path.exists():
            with path.open(encoding="utf-8") as f:
                loaded = json.load(f)
            self._data.update(loaded)

    # --- 予約通知（予約ID単位） ---
    def is_processed(self, reservation_id: str) -> bool:
        return reservation_id in self._data["processed_notifications"]

    def mark_processed(self, reservation_id: str, note: str = "") -> None:
        self._data["processed_notifications"][reservation_id] = {
            "at": datetime.now().isoformat(timespec="seconds"),
            "note": note,
        }

    # --- メール送信済みの検査（患者×検査日） ---
    def is_notified(self, finding_key: str) -> bool:
        return finding_key in self._data["notified_findings"]

    def mark_notified(self, finding_key: str) -> None:
        self._data["notified_findings"][finding_key] = datetime.now().isoformat(timespec="seconds")

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        with tmp.open("w", encoding="utf-8") as f:
            json.dump(self._data, f, ensure_ascii=False, indent=2)
        tmp.replace(self.path)
