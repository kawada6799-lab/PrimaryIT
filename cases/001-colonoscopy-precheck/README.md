# 案件001 precheck：大腸カメラ予約で「事前診察」が無い患者を知らせる

Wakumy の予約通知一覧に「予約確定時」の通知が入ったら、その患者の予約一覧を見て、
大腸の検査予約（例「胃＋大腸カメラ検査」）に対して「大腸カメラ事前診察」の予約が無ければメールで知らせます。

- 動かす場所：**手持ちPC（インターネット側）**。電カルネットワークには触れません。
- 実行時に **AI は使いません**。文字列の比較だけで判定します。
- 要件・判定ルール・画面の手順：`docs/cases/001-colonoscopy-precheck/`

## セットアップ（Windows）

1. Python 3.12 以上を https://www.python.org/ からインストール（「Add python.exe to PATH」にチェック）。
2. このフォルダでコマンドプロンプトを開き：

   ```bat
   python -m venv .venv
   .venv\Scripts\activate
   pip install -e .[dev]
   playwright install chromium
   ```

3. `config.example.toml` を `config.toml` にコピーして、Wakumy の ID、メールの送信元・宛先を書く。
4. パスワードは環境変数で渡す（`config.toml` にも書けるが推奨しない）。`run.bat` を開いて
   `WAKUMY_PASSWORD` と `SMTP_PASSWORD` を埋める。`run.bat` も `config.toml` も git には上がらない。

   - Gmail から送る場合は Google アカウントの「アプリパスワード」を作って `SMTP_PASSWORD` に入れる。

## 動作確認の順番

```bat
:: 1. ブラウザを表示して、特定の患者だけ確認する（メールは送らない）
precheck --config config.toml --patient "姓 名" --headed --dry-run

:: 2. 通知一覧から対象を拾うところまで、メール無しで
precheck --config config.toml --dry-run

:: 3. 本番
precheck --config config.toml
```

1 で「ログイン → 患者管理 → 検索 → 患者ページ → 予約一覧の読み取り」が通れば、画面側はほぼ大丈夫です。
途中で止まる場合は、画面の部品の探し方（`src/precheck/wakumy.py`）を実機に合わせて直します。
ここだけは **Claude Code を先生のPCで動かして**、エラーメッセージと画面を見せながら直すのが早いです（開発時のAI利用）。

## 定期実行（タスクスケジューラ）

1. `run.bat` を右クリック → パスを控える。
2. タスクスケジューラ → 基本タスクの作成 → トリガー「毎日」→ 詳細設定で「繰り返し間隔 30分、継続時間 無期限」。
3. 操作「プログラムの開始」で `run.bat` を指定。「開始（オプション）」にこのフォルダのパス。
4. 「ユーザーがログオンしているかどうかにかかわらず実行する」にチェック。

30 分ごとに予約通知一覧を見て、新しい「予約確定時」通知だけ処理します。
処理済みの予約IDは `state/processed.json` に記録され、同じ患者・同じ検査日には1回しかメールしません。

## 判定ルール（config.toml の [rules] で変更可）

| 項目 | 既定値 | 意味 |
|---|---|---|
| exam_keywords | 大腸 | 予約メニューにこれを含めば「大腸の検査予約」 |
| exam_exclude_keywords | 連鎖用, 事前診察 | 枠確保用の連鎖予約と事前診察そのものは検査から除外 |
| pre_exam_keywords | 大腸カメラ事前診察 | 事前診察とみなすメニュー |
| window_days | 30 | 検査日の前 30 日以内に事前診察があれば OK |
| exam_active_statuses | 予約 | 検査予約として数えるステータス |
| pre_exam_ok_statuses | 予約, 来院 | 事前診察として数えるステータス（済んだ「来院」も含む） |
| notification_max_age_days | 3 | これより古い通知は見ない（初回実行時の暴発防止） |

## 既知の制約

- 予約通知一覧は 1 ページ目だけ読みます。30 分おきに動かす前提なら十分です。
- 同姓同名の患者が複数いる場合は全員を確認し、該当があれば全員分メールします。
- 患者ページの予約一覧が複数ページある場合のページ送りは、実機で未確認です。

## テスト

```bat
pytest
```

判定ロジック・文字列の解釈・状態ファイル・メール本文のテストです。Wakumy には接続しません。
