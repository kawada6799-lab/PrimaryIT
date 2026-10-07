@echo off
chcp 65001 > nul
cd /d %~dp0
if not exist state mkdir state
.venv\Scripts\python.exe -m precheck.cli --config config.toml >> state\run.log 2>&1
