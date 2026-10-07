@echo off
chcp 65001 > nul
cd /d %~dp0
call .venv\Scripts\activate
if not exist state mkdir state
precheck --config config.toml >> state\run.log 2>&1
