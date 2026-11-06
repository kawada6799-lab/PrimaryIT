# GitHub の最新ビルド（Actions の成果物 kenshin-002-windows）を取ってきて、このフォルダを入れ替える。
# インターネットにつながる PC で使う。data\・config\config.json・取込\ は残す。config のマスターは上書きし、前のものは backup\ に残す。
# 使い方：update-from-github.bat をダブルクリック。初回だけ GitHub のトークンを聞く（local\github-token.txt に保存）。
#   トークン：GitHub → Settings → Developer settings → Fine-grained tokens → Generate。Repository access: PrimaryIT だけ。
#   Permissions → Repository permissions → Actions: Read-only。これだけでよい。
# 引数 -ZipOnly を付けると入れ替えずに kenshin-update.zip を作るだけ（USB でオフラインの PC へ運ぶ用）。
param([switch]$ZipOnly)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$owner = 'kawada6799-lab'; $repo = 'PrimaryIT'; $artifact = 'kenshin-002-windows'
$tokenFile = Join-Path $dir 'local\github-token.txt'
if (-not (Test-Path $tokenFile)) {
    Write-Host "GitHub のトークンが未設定です。Fine-grained token（PrimaryIT の Actions: Read-only）を貼り付けてください。"
    $t = Read-Host "トークン"
    New-Item -ItemType Directory -Force (Split-Path $tokenFile) | Out-Null
    Set-Content -Path $tokenFile -Value $t.Trim() -NoNewline
}
$token = (Get-Content $tokenFile -Raw).Trim()
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'kenshin-updater' }
Write-Host "最新のビルドを探しています..."
$list = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$owner/$repo/actions/artifacts?name=$artifact&per_page=5"
$a = $list.artifacts | Where-Object { -not $_.expired } | Sort-Object created_at -Descending | Select-Object -First 1
if (-not $a) { throw "成果物 $artifact が見つかりません（ビルドがまだ無いか、トークンの権限不足）" }
$stamp = ([DateTime]$a.created_at).ToLocalTime().ToString('yyyy-MM-dd HH:mm')
Write-Host "見つかりました: $stamp（$([math]::Round($a.size_in_bytes/1MB)) MB、ブランチ $($a.workflow_run.head_branch)）"
$zip = Join-Path $dir 'kenshin-update.zip'
Invoke-WebRequest -Headers $headers -Uri $a.archive_download_url -OutFile $zip
Write-Host "ダウンロードしました: $zip"
if ($ZipOnly) { Write-Host "この zip を USB でオフラインの PC へ運び、そこの apply-update.bat を実行してください。"; exit 0 }
& (Join-Path $dir 'apply-update.ps1') -Zip $zip
