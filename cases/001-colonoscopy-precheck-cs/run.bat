@echo off
chcp 65001 > nul
cd /d %~dp0
if not exist state mkdir state
precheck.exe --config config.json >> state\run.log 2>&1
