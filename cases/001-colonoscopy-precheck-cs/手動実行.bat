@echo off
chcp 65001 > nul
cd /d %~dp0
precheck.exe --config config.json -v
echo.
echo Press Enter 3 times to close this window.
set /p _=
set /p _=
set /p _=
