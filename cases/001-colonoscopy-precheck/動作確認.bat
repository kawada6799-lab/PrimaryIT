@echo off
chcp 65001 > nul
cd /d %~dp0
if not exist .venv\Scripts\python.exe ( echo 先に setup.bat を実行してください。& pause & exit /b 1 )
echo 動作確認（ファイルは作らず、何も記録しません）
echo   患者名を「姓 名」で入力 → その患者だけ確認
echo   何も入力せず Enter   → 予約通知一覧から対象を拾う本番と同じ流れを確認
set NAME=
set /p NAME=入力: 
if "%NAME%"=="" (
  .venv\Scripts\python.exe -m precheck.cli --config config.toml --headed --dry-run -v
) else (
  .venv\Scripts\python.exe -m precheck.cli --config config.toml --patient "%NAME%" --headed --dry-run -v
)
pause
