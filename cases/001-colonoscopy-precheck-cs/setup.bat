@echo off
chcp 65001 > nul
cd /d %~dp0
if not exist precheck.exe ( echo precheck.exe not found. Put this bat next to precheck.exe. & set /p _= & exit /b 1 )
precheck.exe --config config.json --save-password
echo.
echo Setup done. Next: run the check bat.
echo.
echo Press Enter 3 times to close this window.
set /p _=
set /p _=
set /p _=
