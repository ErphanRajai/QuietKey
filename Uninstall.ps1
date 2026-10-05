$ErrorActionPreference = 'Stop'
$uninstaller = Join-Path $env:LOCALAPPDATA 'QuietKey\app\QuietKey.Uninstall.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) { throw 'QuietKey is not installed. Check Windows Settings > Installed apps.' }
Start-Process -FilePath $uninstaller -WindowStyle Hidden
