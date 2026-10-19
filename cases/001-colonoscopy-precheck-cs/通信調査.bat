@echo off
chcp 65001 > nul
cd /d "%~dp0"
if exist precheck.exe goto :found
echo precheck.exe not found in: %CD%
echo Press Enter 3 times to close this window.
set /p _=
set /p _=
set /p _=
exit /b 1
:found
precheck.exe --config config.json --dump-api
echo.
echo Send only state\debug_api\summary.txt to the developer.
echo Press Enter 3 times to close this window.
set /p _=
set /p _=
set /p _=
