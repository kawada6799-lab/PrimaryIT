@echo off
chcp 65001 > nul
cd /d %~dp0
call .venv\Scripts\activate
echo 予約通知一覧を読んで、該当者がいれば「結果」フォルダにファイルを作ります。
precheck --config config.toml -v
pause
