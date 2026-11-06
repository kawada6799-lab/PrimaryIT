@echo off
rem Download the latest build to kenshin-update.zip only (to carry by USB to the offline PC). ASCII only.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-from-github.ps1" -ZipOnly
pause
