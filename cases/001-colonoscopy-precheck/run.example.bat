@echo off
rem run.bat にコピーして使う（run.bat は git 管理外）
cd /d %~dp0
set WAKUMY_PASSWORD=ここにWakumyのパスワード
set SMTP_PASSWORD=ここにメールのパスワード（Gmailならアプリパスワード）
call .venv\Scripts\activate
precheck --config config.toml >> state\run.log 2>&1
