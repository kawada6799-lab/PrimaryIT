@echo off
rem 健診結果作成を起動する。黒い画面は開いたままにする（閉じると止まる）。
rem 本体は kenshin.dll。同梱した .NET ランタイム（runtime\dotnet.exe、Microsoft 署名付き）で動かす。
rem 署名の無い起動用 exe はウイルス対策の「ふるまい検知」に止められたので作らない。この bat が画面を開く。
rem exe が止まっても黒い画面は残る（エラーを読めるように）。エラーは kenshin.log にも残る。
cd /d "%~dp0"
set "DOTNET_ROOT=%~dp0runtime"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
start "kenshin（閉じると止まります）" cmd /k ""%~dp0runtime\dotnet.exe" kenshin.dll"
timeout /t 3 /nobreak >nul
start "" "http://127.0.0.1:5080/"
