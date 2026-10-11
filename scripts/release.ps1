# Build and pack EQ2Parser locally, for trying an installer before a release.
#
#   ./scripts/release.ps1 -Version 0.1.0
#
# This does NOT publish. Releases are cut by pushing a tag vX.Y.Z, which runs
# .github/workflows/release.yml (tests, attestation, checksums, the GitHub
# release the in-app updater reads). An earlier version of this script could
# publish from a laptop; that flow went to a feed the updater no longer reads
# and has been removed.
#
# Keep this file ASCII-only: Windows PowerShell 5.1 misreads a BOM-less
# script that is not.
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing the vpk tool...'
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw 'vpk install failed' }
}

Write-Host "Publishing v$Version (self-contained win-x64)..."
dotnet publish src/EQ2Parser.App -c Release -r win-x64 --self-contained true `
    -o publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host 'Packing with Velopack...'
vpk pack --packId EQ2Parser --packVersion $Version --packDir publish `
    --mainExe EQ2Parser.App.exe --packTitle EQ2Parser --outputDir Releases `
    --icon src/EQ2Parser.App/Assets/EQ2Parser.ico
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Write-Host 'Packed. Local installer: Releases/EQ2Parser-win-Setup.exe'
Write-Host 'To release, push a tag: git tag -a vX.Y.Z -m "EQ2Parser X.Y.Z"; git push origin vX.Y.Z'
