@echo off
rem Developer use only: starts without the network guard (a red banner is shown). Do not use at the clinic.
cd /d "%~dp0"
set "DOTNET_ROOT=%~dp0runtime"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
start "kenshin - dev (no network guard)" cmd /k ""%~dp0runtime\dotnet.exe" kenshin.dll --no-guard"
timeout /t 3 /nobreak >nul
start "" "http://127.0.0.1:5080/"
