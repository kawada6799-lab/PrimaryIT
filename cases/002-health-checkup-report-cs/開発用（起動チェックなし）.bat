@echo off
rem 開発者の手元で動かすとき用。ネットワーク接続の起動チェックを外す（画面に赤い帯が出る）。配布先では使わない。
cd /d "%~dp0"
kenshin.exe --no-guard
pause
