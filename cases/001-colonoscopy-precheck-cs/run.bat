@echo off
chcp 65001 > nul
cd /d "%~dp0"
if not exist state mkdir state
if exist precheck.exe goto :found
echo %DATE% %TIME% precheck.exe not found in %CD% >> state\run.log
exit /b 1
:found
precheck.exe --config config.json >> state\run.log 2>&1
