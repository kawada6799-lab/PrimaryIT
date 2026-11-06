@echo off
rem Download the latest build from GitHub (needs internet + a GitHub token) and replace this folder. ASCII only.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-from-github.ps1" %*
pause
