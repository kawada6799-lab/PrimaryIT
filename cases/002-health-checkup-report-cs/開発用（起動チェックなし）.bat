@echo off
rem 開発者の手元で動かすとき用。ネットワーク接続の起動チェックを外す（画面に赤い帯が出る）。配布先では使わない。
cd /d "%~dp0"
start "kenshin（開発用）" kenshin.exe --no-guard
timeout /t 3 /nobreak >nul
start "" "http://127.0.0.1:5080/"
