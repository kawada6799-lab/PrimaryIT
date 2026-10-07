"""コマンドライン入口。

  precheck --config config.toml            通常実行（新しい予約確定通知を見てメール）
  precheck --config config.toml --dry-run  メールを送らず結果を表示するだけ
  precheck --config config.toml --patient "姓 名"  通知を見ずに特定患者だけ確認（動作確認用）
"""
from __future__ import annotations

import argparse
import logging
import sys
from datetime import date

from . import config as config_mod
from . import mailer
from .models import Finding, Notification, Patient
from .rules import find_missing_pre_exam, select_notifications
from .state import State
from .wakumy import Wakumy, WakumyError

log = logging.getLogger("precheck")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="precheck", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--config", default="config.toml")
    ap.add_argument("--dry-run", action="store_true", help="メールを送らず、状態ファイルも更新しない")
    ap.add_argument("--patient", help="この患者名だけ確認する（通知一覧は見ない）")
    ap.add_argument("--headed", action="store_true", help="ブラウザを表示して動かす")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

    cfg = config_mod.load(args.config)
    if args.headed:
        cfg.wakumy.headless = False
    state = State(cfg.state_path)
    today = date.today()

    try:
        with Wakumy(cfg.wakumy) as w:
            w.login()
            if args.patient:
                targets = [(None, args.patient)]
            else:
                notes = w.read_notifications()
                selected = [n for n in select_notifications(notes, cfg.rules, today) if not state.is_processed(n.reservation_id)]
                log.info("予約通知 %d 件のうち対象 %d 件", len(notes), len(selected))
                targets = [(n, n.patient_name) for n in selected]

            findings: list[Finding] = []
            seen_patients: set[str] = set()
            for note, name in targets:
                for patient in _patients_for(w, name):
                    if patient.card_no in seen_patients:
                        continue
                    seen_patients.add(patient.card_no)
                    fs = find_missing_pre_exam(patient, cfg.rules, today, triggered_by=note)
                    log.info("%s（%s）: 検査予約の確認 → 事前診察なし %d 件", patient.name, patient.card_no, len(fs))
                    findings.extend(fs)
                if note is not None:
                    state.mark_processed(note.reservation_id, note.patient_name)
    except WakumyError as e:
        log.error("%s", e)
        return 2

    sent = 0
    for f in findings:
        if state.is_notified(f.key):
            log.info("通知済みのためスキップ: %s", f.key)
            continue
        msg = mailer.build_message(f, cfg.mail)
        if args.dry_run or not cfg.mail.enabled:
            print("----- メール（送信しない） -----")
            print(f"件名: {msg['Subject']}\n{msg.get_content()}")
        else:
            mailer.send(msg, cfg.mail)
            sent += 1
            state.mark_notified(f.key)

    if not args.dry_run:
        state.save()
    log.info("完了: 事前診察なし %d 件、メール送信 %d 件", len(findings), sent)
    return 0


def _patients_for(w: Wakumy, name: str) -> list[Patient]:
    """名前で検索し、同姓同名が複数いれば全員を確認対象にする。"""
    hits = w.search_patients(name)
    if not hits:
        log.warning("患者管理で見つかりません: %s", name)
        return []
    if len(hits) > 1:
        log.warning("同姓同名 %d 名: %s（全員を確認します）", len(hits), name)
    patients = []
    for i, h in enumerate(hits):
        card_no = h.get("診察券番号", "")
        if not card_no:
            continue
        if i > 0:
            w.search_patients(name)  # 患者ページから戻って検索し直す
        patients.append(w.open_patient(card_no))
    return patients


if __name__ == "__main__":
    sys.exit(main())
