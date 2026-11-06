@echo off
rem Apply kenshin-update.zip (in this folder or on a USB root) to this folder. No internet needed. ASCII only.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0apply-update.ps1"
pause
