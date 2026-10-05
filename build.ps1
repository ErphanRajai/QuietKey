param([switch]$Test, [switch]$Package)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler was not found. Install .NET Framework 4.8.' }
$output = Join-Path $root 'build'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$refs = @('System.dll','System.Core.dll','System.Management.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:' + (Join-Path $output 'QuietKey.exe')),('/win32manifest:' + (Join-Path $root 'src\app.manifest')))
$icon = Join-Path $root 'assets\QuietKey.ico'
if (Test-Path -LiteralPath $icon) { $arguments += '/win32icon:' + $icon }
foreach ($reference in $refs) { $arguments += '/reference:' + (Join-Path $framework $reference) }
$arguments += '/resource:' + (Join-Path $root 'src\MainWindow.xaml') + ',QuietKey.MainWindow.xaml'
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'QuietKey build failed.' }
Write-Output ('Built ' + (Join-Path $output 'QuietKey.exe'))
$installerArguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/win32manifest:' + (Join-Path $root 'src\app.manifest')))
foreach ($reference in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','Microsoft.CSharp.dll')) { $installerArguments += '/reference:' + (Join-Path $framework $reference) }
if (Test-Path -LiteralPath $icon) { $installerArguments += '/win32icon:' + $icon }
$uninstaller = Join-Path $output 'QuietKey.Uninstall.exe'
$uninstallerArguments = $installerArguments + @(('/out:' + $uninstaller), (Join-Path $root 'installer\Uninstall.cs'), (Join-Path $root 'installer\InstallerCommon.cs'))
& $compiler $uninstallerArguments
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller build failed.' }
$setup = Join-Path $output 'QuietKey.Setup.exe'
$setupArguments = $installerArguments + @(('/out:' + $setup), ('/resource:' + (Join-Path $output 'QuietKey.exe') + ',QuietKey.Payload.App.exe'), ('/resource:' + $uninstaller + ',QuietKey.Payload.Uninstall.exe'), (Join-Path $root 'installer\Setup.cs'), (Join-Path $root 'installer\InstallerCommon.cs'))
& $compiler $setupArguments
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Write-Output ('Built ' + $setup)
if ($Test) {
    $testArgs = @('/nologo','/target:exe','/platform:x64','/utf8output',('/out:' + (Join-Path $output 'QuietKey.Tests.exe')))
    foreach ($reference in $refs) { $testArgs += '/reference:' + (Join-Path $framework $reference) }
    $testArgs += @((Join-Path $root 'src\Models.cs'), (Join-Path $root 'src\NightLight.cs'), (Join-Path $root 'src\WindowsServices.cs'), (Join-Path $root 'src\Modes.cs'), (Join-Path $root 'tests\Tests.cs'))
    & $compiler $testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $output 'QuietKey.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if ($Package) {
    $packageDirectory = Join-Path $output 'package'
    $distribution = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $packageDirectory, $distribution -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $output 'QuietKey.exe') -Destination (Join-Path $packageDirectory 'QuietKey.exe') -Force
    Copy-Item -LiteralPath $setup -Destination (Join-Path $packageDirectory 'QuietKey.Setup.exe') -Force
    foreach ($name in @('Install.ps1','Uninstall.ps1','README.md')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $packageDirectory $name) -Force }
    $packageDocs = Join-Path $packageDirectory 'docs'
    New-Item -ItemType Directory -Path $packageDocs -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs\GUIDE.md') -Destination (Join-Path $packageDocs 'GUIDE.md') -Force
    Copy-Item -LiteralPath $setup -Destination (Join-Path $distribution 'QuietKey.Setup.exe') -Force
    $archive = Join-Path $distribution 'QuietKey-1.1.0-win-x64.zip'
    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archive -Force
    Write-Output ('Packaged ' + $archive)
}
