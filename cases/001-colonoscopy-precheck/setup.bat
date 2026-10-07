@echo off
chcp 65001 > nul
cd /d %~dp0
echo === precheck セットアップ（初回だけ） ===
echo.

rem --- Python を探す。Microsoft Store の偽 python に騙されないよう、実際に --version が通るものだけ採用する
set PY=
py -3 --version > nul 2>&1 && set PY=py -3
if not defined PY ( python --version > nul 2>&1 && set PY=python )
if not defined PY (
  echo [エラー] Python が見つかりません。
  echo   https://www.python.org/downloads/ からインストールしてください。
  echo   インストーラの最初の画面で「Add python.exe to PATH」に必ずチェックを入れます。
  echo   入れ終わったら、この黒い画面を閉じてから setup.bat をもう一度実行してください。
  pause & exit /b 1
)
for /f "tokens=*" %%v in ('%PY% --version 2^>^&1') do echo 使う Python: %%v  （%PY%）

rem --- 仮想環境
if not exist .venv\Scripts\python.exe (
  echo 仮想環境を作成しています...
  %PY% -m venv .venv
)
if not exist .venv\Scripts\python.exe (
  echo [エラー] 仮想環境を作れませんでした。Python のインストールをやり直してください。
  pause & exit /b 1
)
set VPY=.venv\Scripts\python.exe

echo 必要な部品を入れています（数分かかります）...
%VPY% -m pip install -q --upgrade pip
%VPY% -m pip install -q -e .[dev] || (echo [エラー] pip install に失敗しました。インターネット接続を確認してください。& pause & exit /b 1)
%VPY% -m playwright install chromium || (echo [エラー] ブラウザの取得に失敗しました。インターネット接続を確認してください。& pause & exit /b 1)

echo.
%VPY% -m precheck.cli --save-password
echo.
echo セットアップ完了。次は 動作確認.bat を実行してください。
pause
