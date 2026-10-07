@echo off
chcp 65001 > nul
cd /d %~dp0
if not exist .venv\Scripts\python.exe ( echo Run setup.bat first. & echo. & echo Press Enter 3 times to close this window. & set /p _= & set /p _= & set /p _= & exit /b 1 )
.venv\Scripts\python.exe -m precheck.cli --config config.toml --check
echo.
echo Press Enter 3 times to close this window.
set /p _=
set /p _=
set /p _=
