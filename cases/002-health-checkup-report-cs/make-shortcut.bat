@echo off
rem Create a desktop shortcut "KenshinApp" that starts run.bat (minimized) with kenshin.ico.
rem Run this once after placing the folder (e.g. C:\kenshin). ASCII only.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-shortcut.ps1"
pause
