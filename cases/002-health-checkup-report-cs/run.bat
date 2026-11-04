@echo off
rem 健診結果作成を起動する。黒い画面は開いたままにする（閉じると止まる）。
cd /d "%~dp0"
kenshin.exe
pause
