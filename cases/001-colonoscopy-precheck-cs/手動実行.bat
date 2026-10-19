@echo off
chcp 65001 > nul
cd /d "%~dp0"
if exist precheck.exe goto :found
echo precheck.exe not found in: %CD%
echo  - If you opened this bat inside the ZIP, extract the ZIP first (right click, Extract All).
echo  - If precheck.exe was in this folder before, your antivirus (Norton, Windows Security) may have removed it.
echo    Open its quarantine / protection history and restore precheck.exe, then exclude this folder.
echo Press Enter 3 times to close this window.
set /p _=
set /p _=
set /p _=
exit /b 1
:found
precheck.exe --config config.json -v
echo.
echo Press Enter 3 times to close this window.
set /p _=
set /p _=
set /p _=
