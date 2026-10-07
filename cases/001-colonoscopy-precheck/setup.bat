@echo off
chcp 65001 > nul
cd /d %~dp0
echo === precheck セットアップ（初回だけ） ===
where python > nul 2>&1 || (echo Python が見つかりません。https://www.python.org/ から 3.12 以上を入れて「Add python.exe to PATH」にチェックしてください。& pause & exit /b 1)
if not exist .venv ( python -m venv .venv )
call .venv\Scripts\activate
pip install -q -e .[dev] || (echo pip install に失敗しました & pause & exit /b 1)
playwright install chromium || (echo ブラウザの取得に失敗しました & pause & exit /b 1)
echo.
precheck --save-password
echo.
echo セットアップ完了。次は 動作確認.bat を実行してください。
pause
