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

    # --- 知らせた回数（患者×検査日） ---
    def is_notified(self, finding_key: str) -> bool:
        return self.notified_count(finding_key) > 0

    def notified_count(self, finding_key: str) -> int:
        v = self._data["notified_findings"].get(finding_key)
        if v is None:
            return 0
        if isinstance(v, dict):
            return int(v.get("count", 1))
        return 1  # 旧形式（日時の文字列だけ）は 1 回とみなす

    def mark_notified(self, finding_key: str) -> None:
        n = self.notified_count(finding_key) + 1
        self._data["notified_findings"][finding_key] = {"count": n, "last": datetime.now().isoformat(timespec="seconds")}

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        with tmp.open("w", encoding="utf-8") as f:
            json.dump(self._data, f, ensure_ascii=False, indent=2)
        tmp.replace(self.path)
