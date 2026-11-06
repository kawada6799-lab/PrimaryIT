# デスクトップに「健診結果作成」のショートカットを作る（run.bat を最小化で起動、アイコンは kenshin.ico）。
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$desktop = [Environment]::GetFolderPath('Desktop')
$lnk = Join-Path $desktop '健診結果作成.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = Join-Path $dir 'run.bat'
$s.WorkingDirectory = $dir
$s.IconLocation = (Join-Path $dir 'kenshin.ico') + ',0'
$s.WindowStyle = 7   # 最小化
$s.Description = '健診結果作成（kenshin）'
$s.Save()
Write-Host "作成しました: $lnk"
