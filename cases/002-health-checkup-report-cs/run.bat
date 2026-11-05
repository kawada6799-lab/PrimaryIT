@echo off
rem Start kenshin (health checkup). Keep the black window open; closing it stops the program.
rem The app is kenshin.dll, run by the bundled .NET runtime (runtime\dotnet.exe, signed by Microsoft).
rem This file is ASCII only: Japanese text in a .bat breaks on cmd.exe (code page mismatch).
cd /d "%~dp0"
set "DOTNET_ROOT=%~dp0runtime"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
start "kenshin - keep this window open" cmd /k ""%~dp0runtime\dotnet.exe" kenshin.dll"
timeout /t 3 /nobreak >nul
start "" "http://127.0.0.1:5080/"
