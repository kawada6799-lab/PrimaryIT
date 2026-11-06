@echo off
rem Start kenshin (health checkup). Window is minimized; use the "Exit" button in the app to stop.
rem The app is kenshin.dll, run by the bundled .NET runtime (runtime\dotnet.exe, signed by Microsoft).
rem This file is ASCII only: Japanese text in a .bat breaks on cmd.exe (code page mismatch).
cd /d "%~dp0"
set "DOTNET_ROOT=%~dp0runtime"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
start /min "kenshin" cmd /c ""%~dp0runtime\dotnet.exe" kenshin.dll || pause"
timeout /t 3 /nobreak >nul
set "EDGE=%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"
if not exist "%EDGE%" set "EDGE=%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
if exist "%EDGE%" (
  start "" "%EDGE%" --app=http://127.0.0.1:5080/ --window-size=1366,900
) else (
  start "" "http://127.0.0.1:5080/"
)
