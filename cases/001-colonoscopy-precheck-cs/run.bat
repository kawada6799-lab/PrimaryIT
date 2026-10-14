@echo off
chcp 65001 > nul
cd /d "%~dp0"
if not exist state mkdir state
if not exist precheck.exe ( echo %DATE% %TIME% precheck.exe not found in %CD% >> state\run.log & exit /b 1 )
precheck.exe --config config.json >> state\run.log 2>&1
