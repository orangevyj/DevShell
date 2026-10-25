<#
    Builds the DevShell setup with Inno Setup 6.

    DevShell is published self-contained first, so the runtime travels inside the setup and the
    machine it is installed on needs nothing downloaded. Inno Setup then compresses that publish
    output into one wizard, whose script is DevShell.iss beside this file.

    Usage, from the repository root:
        powershell -ExecutionPolicy Bypass -File Installer\build-setup.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $InnoSetup
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'DevShell.iss'
$output = Join-Path $root 'dist'
$publish = Join-Path $env:TEMP "devshell-publish-$PID"

if (-not $InnoSetup) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    $InnoSetup = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $InnoSetup) {
    throw "Inno Setup's command-line compiler was not found; install Inno Setup 6 or pass -InnoSetup <path to ISCC.exe>."
}

Write-Host "== Inno Setup: $InnoSetup" -ForegroundColor DarkGray

try {
    Write-Host '== DevShell, self-contained ==' -ForegroundColor Cyan
    & dotnet publish (Join-Path $root 'DevShell.csproj') -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'the DevShell publish failed' }

    Write-Host '== the wizard ==' -ForegroundColor Cyan
    & $InnoSetup "/DPublishDir=$publish" "/DOutputDir=$output" $script
    if ($LASTEXITCODE -ne 0) { throw 'the Inno Setup compile failed' }
}
finally {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
}

$setup = Join-Path $output 'DevShell.Setup.exe'
$setupMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "setup: $setup ($setupMb MB)" -ForegroundColor Green
