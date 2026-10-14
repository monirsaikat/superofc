<#
.SYNOPSIS
  Builds, tests and packages SuperOfc into a Velopack installer and update feed (docs/07).

.EXAMPLE
  ./tools/release/publish.ps1 -Version 0.1.0-beta.1 -Channel beta
  ./tools/release/publish.ps1 -Version 1.0.0 -Channel stable -SignParams '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'

.NOTES
  Requires: .NET 10 SDK, vpk (dotnet tool install -g vpk --version 1.2.161).
  Output: artifacts/releases/<channel>/ (Setup.exe, full/delta packages, releases.<channel>.json).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('stable', 'beta')][string]$Channel = 'beta',
    [string]$SignParams = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$bridgeDir = Join-Path $publishDir 'bridge'
$releaseDir = Join-Path $artifacts "releases\$Channel"
$notesFile = Join-Path $artifacts 'RELEASE-NOTES.md'

function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Title failed with exit code $LASTEXITCODE" }
}

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Force $publishDir, $releaseDir | Out-Null

if (-not $SkipTests) {
    Invoke-Step 'Build and test' {
        dotnet build (Join-Path $root 'SuperOfc.sln') -c Release -warnaserror -nologo
        if ($LASTEXITCODE -eq 0) { dotnet test (Join-Path $root 'SuperOfc.sln') -c Release --no-build -nologo }
    }
}

Invoke-Step 'Publish desktop (win-x64, self-contained, ReadyToRun)' {
    dotnet publish (Join-Path $root 'src\SuperOfc.Desktop\SuperOfc.Desktop.csproj') -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:Version=$Version -o $publishDir -nologo
}

# The bridge targets .NET Framework 4.8 (x86), which Windows 10 1903+ and Windows 11 include, so it is not self-contained (ADR-06).
Invoke-Step 'Publish device bridge (net48, x86)' {
    dotnet publish (Join-Path $root 'src\SuperOfc.DeviceBridge\SuperOfc.DeviceBridge.csproj') -c Release `
        -p:Version=$Version -o $bridgeDir -nologo
}
if (-not (Test-Path (Join-Path $bridgeDir 'sdk\zkemkeeper.dll'))) {
    Write-Warning 'ZKTeco SDK not found in src\SuperOfc.DeviceBridge\sdk. Devices will only work through file import (see sdk\README.md).'
}

# Release notes: the CHANGELOG section for this version, or [Unreleased] when not yet cut.
$changelog = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw
$pattern = "(?ms)^## \[(?:$([regex]::Escape($Version))|Unreleased)\][^\n]*\n(.*?)(?=^## \[|\z)"
$match = [regex]::Match($changelog, $pattern)
$notes = if ($match.Success) { $match.Groups[1].Value.Trim() } else { "SuperOfc $Version" }
Set-Content -Path $notesFile -Value $notes -Encoding utf8

$packArgs = @(
    'pack', '--packId', 'SuperOfc', '--packVersion', $Version, '--packDir', $publishDir,
    '--mainExe', 'SuperOfc.Desktop.exe', '--packTitle', 'SuperOfc', '--packAuthors', 'SuperOfc',
    '--icon', (Join-Path $root 'assets\app.ico'), '--channel', $Channel, '--outputDir', $releaseDir,
    '--releaseNotes', $notesFile, '--runtime', 'win-x64'
)
if ($SignParams) { $packArgs += @('--signParams', $SignParams) }
else { Write-Warning 'Packaging UNSIGNED. Never ship unsigned builds to customers (docs/07, Q19).' }

Invoke-Step 'Velopack pack' { vpk @packArgs }

Write-Host "Done. Installer and feed: $releaseDir" -ForegroundColor Green
Get-ChildItem $releaseDir | Select-Object Name, Length | Format-Table -AutoSize
