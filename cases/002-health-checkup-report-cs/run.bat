@echo off
rem 健診結果作成を起動する。黒い画面は開いたままにする（閉じると止まる）。
rem kenshin.exe 自身はブラウザを起動しない（ウイルス対策の「ふるまい検知」に引っかかりやすいため）。この bat が画面を開く。
rem exe が止まっても黒い画面は残る（エラーを読めるように）。エラーは kenshin.log にも残る。
cd /d "%~dp0"
start "kenshin（閉じると止まります）" cmd /k kenshin.exe
timeout /t 3 /nobreak >nul
start "" "http://127.0.0.1:5080/"
