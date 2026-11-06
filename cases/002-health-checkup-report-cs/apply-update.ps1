# kenshin-update.zip（Actions の成果物の zip）でこのフォルダを入れ替える。インターネット不要。
# 残すもの：data\（DB・発行 PDF・証明書）、config\config.json（院の設定）、取込\、local\。
# 上書きするもの：プログラム本体、runtime\、wwwroot\、fonts\、models\ocr\、config のマスター（判定基準・判定区分・所見・コメント・血液項目・コース）。
#   上書き前の config\ は backup\config-<日時>\ に残す（マスターを手で直していたら、そこから戻せる）。
# 使い方：apply-update.bat をダブルクリック。zip はこのフォルダか USB のルートに置く。
param([string]$Zip = '', [switch]$NoStart)
$ErrorActionPreference = 'Stop'
$dir = (Get-Item (Split-Path -Parent $MyInvocation.MyCommand.Path)).FullName
if ($Zip -eq '') {
    $cands = @(Join-Path $dir 'kenshin-update.zip')
    foreach ($d in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -ne (Split-Path $dir -Qualifier) + '\' })) { $cands += Join-Path $d.Root 'kenshin-update.zip' }
    $Zip = $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Zip) { throw "kenshin-update.zip が見つかりません（このフォルダか USB のルートに置いてください）" }
}
Write-Host "更新ファイル: $Zip"
# 1. 動いていれば止める（PC の画面の「終了」と同じ。バックアップを書いてから止まる）
try { Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5080/api/shutdown' -TimeoutSec 5 | Out-Null; Write-Host "プログラムを止めました"; Start-Sleep -Seconds 3 } catch { }
Get-Process dotnet -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith((Join-Path $dir 'runtime'), [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Stop-Process -Id $_.Id -Force; Write-Host "残っていたプロセスを止めました" }
Start-Sleep -Seconds 1
# 2. 展開
$tmp = Join-Path $env:TEMP ("kenshin-update-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Expand-Archive -Path $Zip -DestinationPath $tmp -Force
# TEMP が短い形式（RUNNER~1 など）のことがあるので、長い形式のフルパスに揃える（相対パスの計算に使う）
$tmp = (Get-Item $tmp).FullName
$src = $tmp
if (-not (Test-Path (Join-Path $src 'kenshin.dll'))) { $inner = Get-ChildItem $tmp -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'kenshin.dll') } | Select-Object -First 1; if ($inner) { $src = $inner.FullName } else { throw "zip の中に kenshin.dll がありません" } }
# 3. config を退避
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$bk = Join-Path $dir "backup\config-$stamp"
New-Item -ItemType Directory -Force $bk | Out-Null
Copy-Item (Join-Path $dir 'config\*') $bk -Recurse -Force -ErrorAction SilentlyContinue
# 4. 上書き（残すものを除く）
$keep = @('data', 'config\config.json', '取込', 'local', 'backup', 'kenshin-update.zip', 'kenshin.log')
Get-ChildItem $src -Recurse -File | ForEach-Object {
    if (-not $_.FullName.StartsWith($src, [StringComparison]::OrdinalIgnoreCase)) { throw "展開先のパスが一致しません: $($_.FullName) / $src" }
    $rel = $_.FullName.Substring($src.Length).TrimStart('\')
    if ($keep | Where-Object { $rel -eq $_ -or $rel.StartsWith($_ + '\') }) { return }
    $dest = Join-Path $dir $rel
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $_.FullName $dest -Force
}
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
$ver = (Get-Item (Join-Path $dir 'kenshin.dll')).LastWriteTime.ToString('yyyy-MM-dd HH:mm')
Write-Host "入れ替えました（プログラムの日時: $ver）。前の config は $bk に残しています。"
# 5. 起動
if ($NoStart) { exit 0 }
Start-Process -FilePath (Join-Path $dir 'run.bat') -WorkingDirectory $dir -WindowStyle Minimized
Write-Host "起動しました。画面右上の v と日時が新しくなっていれば更新は成功です。"
