param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'QuietKey.Setup.exe'
if (-not (Test-Path -LiteralPath $setup)) { $setup = Join-Path $PSScriptRoot 'build\QuietKey.Setup.exe' }
if (-not (Test-Path -LiteralPath $setup)) { & (Join-Path $PSScriptRoot 'build.ps1') }
if (-not (Test-Path -LiteralPath $setup)) { throw 'Build QuietKey before installing it.' }
$setupArguments = @('--quiet')
if ($NoLaunch) { $setupArguments += '--no-launch' }
$process = Start-Process -FilePath $setup -ArgumentList $setupArguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) {
    $log = Join-Path ([IO.Path]::GetTempPath()) 'QuietKey.Setup.error.log'
    if (Test-Path -LiteralPath $log) { throw (Get-Content -LiteralPath $log -Raw) }
    throw 'QuietKey setup failed.'
}
Write-Output 'QuietKey 1.1.0 installed with Windows startup, desktop and Start menu shortcuts, and an Installed apps uninstaller.'
