@echo off
chcp 65001 > nul
cd /d %~dp0
if not exist .venv\Scripts\python.exe ( echo 先に setup.bat を実行してください。& pause & exit /b 1 )
set /p NAME=確認したい患者名を「姓 名」で入力して Enter: 
echo ブラウザが開きます。ログイン → 患者管理 → 検索 → 患者ページ → 予約一覧 と進めば成功です。
.venv\Scripts\python.exe -m precheck.cli --config config.toml --patient "%NAME%" --headed --dry-run -v
pause
