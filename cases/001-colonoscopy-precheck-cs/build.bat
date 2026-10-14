@echo off
chcp 65001 > nul
cd /d %~dp0
rem Build the distributable folder (dist). Requires .NET SDK 8 on the developer PC: https://dotnet.microsoft.com/download
dotnet publish src\Precheck\Precheck.csproj -c Release -o dist
copy /y config.json dist\ > nul
copy /y setup.bat dist\ > nul
copy /y "動作確認.bat" dist\ > nul
copy /y "手動実行.bat" dist\ > nul
copy /y run.bat dist\ > nul
copy /y README.md dist\ > nul
echo.
echo dist folder is ready. Zip the dist folder and hand it over.
echo.
echo Press Enter 3 times to close this window.
set /p _=
set /p _=
set /p _=
