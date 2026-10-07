"""コマンドライン入口。

  precheck --config config.toml            通常実行（新しい予約確定通知を見て結果ファイルを作る）
  precheck --config config.toml --dry-run  メールを送らず結果を表示するだけ
  precheck --config config.toml --patient "姓 名"  通知を見ずに特定患者だけ確認（動作確認用）
  precheck --save-password                 Wakumy のパスワードを入力して保存（初回だけ）
"""
from __future__ import annotations

import argparse
import getpass
import logging
import sys
from pathlib import Path
from datetime import date, timedelta

from . import config as config_mod
from . import mailer, notify
from .models import Finding, Notification, Patient
from .rules import find_missing_pre_exam, is_pre_exam, select_notifications, upcoming_exams
from .state import State
from .wakumy import Wakumy, WakumyError

log = logging.getLogger("precheck")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="precheck", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--config", default="config.toml")
    ap.add_argument("--dry-run", action="store_true", help="ファイルもメールも出さず画面に表示するだけ。状態ファイルも更新しない")
    ap.add_argument("--patient", help="この患者名だけ確認する（通知一覧は見ない）")
    ap.add_argument("--headed", action="store_true", help="ブラウザを表示して動かす")
    ap.add_argument("--save-password", action="store_true", help="Wakumy のパスワードを入力して local/ に保存する（初回だけ）")
    ap.add_argument("--check", action="store_true", help="動作確認モード。患者名を聞いて --headed --dry-run -v で動かす")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    if args.check:
        print("動作確認（ファイルは作らず、何も記録しません）")
        print("  患者名を「姓 名」で入力 → その患者だけ確認")
        print("  何も入力せず Enter       → 予約通知一覧から対象を拾う、本番と同じ流れを確認")
        name = input("入力: ").strip()
        args.patient = name or None
        args.headed = True
        args.dry_run = True
        args.verbose = True
        print("ブラウザが開きます。自動で進むので、触らずに待ってください。")

    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

    if args.save_password:
        pw = getpass.getpass("Wakumy のパスワード（画面には表示されません）: ")
        if not pw.strip():
            print("空なので保存しませんでした。")
            return 1
        saved = config_mod.save_password(Path(args.config).resolve().parent, pw)
        print(f"保存しました: {saved}（このファイルは GitHub には上がりません）")
        return 0

    cfg = config_mod.load(args.config)
    if args.headed:
        cfg.wakumy.headless = False
    state = State(cfg.state_path)
    today = date.today()

    try:
        with Wakumy(cfg.wakumy) as w:
            w.debug_dir = cfg.state_path.parent
            w.login()
            if args.patient:
                targets = [(None, args.patient)]
            else:
                oldest = today - timedelta(days=cfg.rules.notification_max_age_days)
                notes = w.read_notifications(oldest=oldest, kind=cfg.rules.notification_kind)
                selected = [n for n in select_notifications(notes, cfg.rules, today) if not state.is_processed(n.reservation_id)]
                log.info("予約通知 %d 件を読み取り。「%s」で %d 日以内かつ未処理のもの %d 件",
                         len(notes), cfg.rules.notification_kind, cfg.rules.notification_max_age_days, len(selected))
                for n in notes:
                    log.debug("  通知 %s %s %s %s", n.reservation_id, n.kind, n.completed_at, n.patient_name)
                if not notes:
                    log.warning("予約通知一覧が 1 件も読めませんでした。画面の表の構造が想定と違う可能性があります。")
                targets = [(n, n.patient_name) for n in selected]

            findings: list[Finding] = []
            not_found: list[Notification] = []
            seen_patients: set[str] = set()
            for note, name in targets:
                patients = _patients_for(w, name)
                if not patients and note is not None:
                    not_found.append(note)  # 深追いせず、予約IDを結果に載せて手で確認してもらう
                for patient in patients:
                    if patient.card_no in seen_patients:
                        continue
                    seen_patients.add(patient.card_no)
                    fs = find_missing_pre_exam(patient, cfg.rules, today, triggered_by=note)
                    exams = upcoming_exams(patient, cfg.rules, today)
                    pre = [r for r in patient.reservations if is_pre_exam(r, cfg.rules)]
                    log.info(
                        "%s（%s）: 予約一覧 %d 件を読み取り（今後の大腸検査 %d 件、事前診察 %d 件）→ 事前診察なし %d 件",
                        patient.name, patient.card_no, len(patient.reservations), len(exams), len(pre), len(fs),
                    )
                    for r in patient.reservations:
                        log.debug("  %s %s %s %s", r.status, r.start.strftime("%Y/%m/%d %H:%M"), r.department, r.menu)
                    if not patient.reservations:
                        log.warning("予約一覧が 1 件も読めませんでした。画面の表の構造が想定と違う可能性があります。")
                    findings.extend(fs)
                if note is not None:
                    state.mark_processed(note.reservation_id, note.patient_name)
    except WakumyError as e:
        log.error("%s", e)
        return 2

    new_findings = [f for f in findings if not state.is_notified(f.key)]
    for f in findings:
        if f not in new_findings:
            log.info("通知済みのためスキップ: %s", f.key)

    new_not_found = [n for n in not_found if not state.is_notified(f"nf:{n.reservation_id}")]
    report = notify.build_report(new_findings, today, new_not_found)
    if args.dry_run:
        print("----- 結果（ファイルには書かない） -----")
        print(report)
    elif new_findings or new_not_found or cfg.notify.write_empty:
        path = notify.write_report(report, cfg.notify.output_dir, today)
        log.info("結果ファイル: %s", path)
        if (new_findings or new_not_found) and cfg.notify.toast:
            notify.toast("大腸カメラ事前診察チェック", f"事前診察なし {len(new_findings)} 名、要手動確認 {len(new_not_found)} 件。")
        if (new_findings or new_not_found) and cfg.notify.open_after:
            notify.open_file(path)

    sent = 0
    if cfg.mail.enabled and not args.dry_run:
        for f in new_findings:
            mailer.send(mailer.build_message(f, cfg.mail), cfg.mail)
            sent += 1

    if not args.dry_run:
        for f in new_findings:
            state.mark_notified(f.key)
        for n in new_not_found:
            state.mark_notified(f"nf:{n.reservation_id}")

    if not args.dry_run:
        state.save()
    log.info("完了: 事前診察なし %d 件（新規 %d 件）、要手動確認 %d 件、メール送信 %d 件", len(findings), len(new_findings), len(new_not_found), sent)
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
        patients.append(w.open_patient(card_no, fallback_name=name))
    return patients


if __name__ == "__main__":
    sys.exit(main())
