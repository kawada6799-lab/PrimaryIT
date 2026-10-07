@echo off
chcp 65001 > nul
cd /d %~dp0
if not exist .venv\Scripts\python.exe ( echo 先に setup.bat を実行してください。& pause & exit /b 1 )
echo 予約通知一覧を読んで、該当者がいれば「結果」フォルダにファイルを作ります。
.venv\Scripts\python.exe -m precheck.cli --config config.toml -v
pause
