<#
.SYNOPSIS
  Build, test, publish and package Lintel. Works on Windows PowerShell 5.1+ and PowerShell 7.

.EXAMPLE
  ./build.ps1 -All                 # build + test + publish + release assets (Lintel-win-x64.zip, install.ps1, SHA256SUMS.txt)
  ./build.ps1 -Test                # build + test
  ./build.ps1 -Publish -Zip        # self-contained exe + zip + install.ps1 + SHA256SUMS.txt
  ./build.ps1 -ShowPlan -All       # print the commands, change nothing (incl. no SDK download)
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [string]$ArtifactsDir = 'artifacts',
    [switch]$Test,
    [switch]$Publish,
    [switch]$Zip,
    [switch]$All,
    [switch]$Clean,
    [switch]$CompressExe,
    [switch]$ShowPlan
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot

# ---------------------------------------------------------------- helpers (dot-source friendly)

function Get-DotnetSdkVersions([string]$DotnetPath) {
    # Returns [version[]] of installed SDKs reported by `<dotnet> --list-sdks`; empty on any failure.
    try {
        $lines = & $DotnetPath --list-sdks 2>$null
        $out = @()
        foreach ($l in @($lines)) {
            if ("$l" -match '^(\d+\.\d+\.\d+)') { $out += [version]$Matches[1] }
        }
        return , $out
    } catch { return , @() }
}

function Test-Sdk10([version[]]$Versions) {
    # global.json asks for 10.0.100+ (rollForward latestFeature)
    foreach ($v in $Versions) { if ($v.Major -eq 10 -and $v -ge [version]'10.0.100') { return $true } }
    return $false
}

# Resolution order: dotnet 10 on PATH -> <repo>\.dotnet\dotnet.exe -> bootstrap into <repo>\.dotnet.
# Returns @{ Path; Source = 'PATH'|'local'|'bootstrap' }. Injectable for tests.
function Resolve-Dotnet(
    [string]$RepoRoot,
    [scriptblock]$FindPathDotnet = { $c = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1; if ($c) { $c.Source } },
    [scriptblock]$ListSdks = { param($p) Get-DotnetSdkVersions $p },
    [scriptblock]$Bootstrap = { param($root) Install-DotnetSdk $root }
) {
    $onPath = & $FindPathDotnet
    if ($onPath -and (Test-Sdk10 (& $ListSdks $onPath))) { return @{ Path = $onPath; Source = 'PATH' } }

    $local = Join-Path $RepoRoot '.dotnet\dotnet.exe'
    if ((Test-Path $local) -and (Test-Sdk10 (& $ListSdks $local))) { return @{ Path = $local; Source = 'local' } }

    $path = & $Bootstrap $RepoRoot
    return @{ Path = $path; Source = 'bootstrap' }
}

# Command lines the bootstrap runs (separate so -ShowPlan and tests can print them without downloading).
function Get-BootstrapPlan([string]$RepoRoot) {
    $dir = Join-Path $RepoRoot '.dotnet'
    @{
        Url      = 'https://dot.net/v1/dotnet-install.ps1'
        Script   = Join-Path $dir 'dotnet-install.ps1'
        InstallDir = $dir
        Primary  = @('-JSonFile', (Join-Path $RepoRoot 'global.json'), '-InstallDir', $dir, '-NoPath')
        Fallback = @('-Channel', '10.0', '-InstallDir', $dir, '-NoPath')
        Dotnet   = Join-Path $dir 'dotnet.exe'
    }
}

function Install-DotnetSdk([string]$RepoRoot) {
    $p = Get-BootstrapPlan $RepoRoot
    New-Item -ItemType Directory -Force -Path $p.InstallDir | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Write-Host "No .NET 10 SDK found. Downloading $($p.Url) and installing into $($p.InstallDir) ..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing -Uri $p.Url -OutFile $p.Script
    try { & $p.Script @($p.Primary) } catch {
        Write-Warning "dotnet-install with global.json failed ($($_.Exception.Message)); retrying with -Channel 10.0"
        & $p.Script @($p.Fallback)
    }
    if (-not (Test-Path $p.Dotnet)) { throw "dotnet-install did not produce $($p.Dotnet)" }
    return $p.Dotnet
}

function Get-DefaultVersion([string]$RepoRoot) {
    $props = Get-Content -Raw (Join-Path $RepoRoot 'Directory.Build.props')
    if ($props -match '<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>') { return $Matches[1] }
    throw 'VersionPrefix not found in Directory.Build.props'
}

function Write-Sha256Sums([string]$Dir, [string[]]$Files) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in $Files) {
        $h = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Dir $f)).Hash.ToLowerInvariant()
        [void]$sb.Append("$h  $f`n")
    }
    [IO.File]::WriteAllText((Join-Path $Dir 'SHA256SUMS.txt'), $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
}

# Dot-sourced (tests): define functions only.
if ($MyInvocation.InvocationName -eq '.') { return }

# ---------------------------------------------------------------- main

function Invoke-Step([string]$Title, [string]$Exe, [string[]]$Arguments) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    Write-Host "    $Exe $($Arguments -join ' ')" -ForegroundColor DarkGray
    if ($ShowPlan) { return }
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)" }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

if ($All) { $Test = $true; $Publish = $true; $Zip = $true }
if ($Zip) { $Publish = $true }
if (-not $Version) { $Version = Get-DefaultVersion $Root }
if ($Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.\-+]+)?$') { throw "Version '$Version' is not semver (e.g. 1.2.3 or 1.2.3-rc.1)" }

$Artifacts   = if ([IO.Path]::IsPathRooted($ArtifactsDir)) { $ArtifactsDir } else { Join-Path $Root $ArtifactsDir }
$PublishDir  = Join-Path $Artifacts "publish\$Runtime"
$ReleaseDir  = Join-Path $Artifacts 'release'
$Sln         = Join-Path $Root 'Lintel.slnx'
$AppProj     = Join-Path $Root 'src\Lintel.App\Lintel.App.csproj'

# 1. SDK
if ($ShowPlan) {
    $dotnet = 'dotnet'
    $bp = Get-BootstrapPlan $Root
    Write-Host "SDK: PATH dotnet 10 -> $($bp.Dotnet) -> bootstrap:" -ForegroundColor Cyan
    Write-Host "    $($bp.Url) -> $($bp.Script)"
    Write-Host "    & '$($bp.Script)' $($bp.Primary -join ' ')   (fallback: $($bp.Fallback -join ' '))"
} else {
    $sdk = Resolve-Dotnet $Root
    $dotnet = $sdk.Path
    Write-Host "SDK: $dotnet ($($sdk.Source))" -ForegroundColor Cyan
    # child MSBuild/host processes must find the same runtime
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

Push-Location $Root
try {
    if ($Clean -and -not $ShowPlan) {
        foreach ($d in @($PublishDir, $ReleaseDir)) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
    }
    $common = @("-c", $Configuration, "-p:Version=$Version", "--artifacts-path", $Artifacts)
    Invoke-Step 'restore' $dotnet (@('restore', $Sln, "--artifacts-path", $Artifacts))
    Invoke-Step 'build' $dotnet (@('build', $Sln, '--no-restore') + $common)
    if ($Test) { Invoke-Step 'test' $dotnet (@('test', $Sln, '--no-build') + $common) }

    if ($Publish) {
        $compress = if ($CompressExe) { 'true' } else { 'false' }
        Invoke-Step 'publish' $dotnet @('publish', $AppProj, '-c', $Configuration, '-r', $Runtime, '--self-contained',
            "-p:Version=$Version", "-p:CompressExe=$compress", '--artifacts-path', $Artifacts, '-o', $PublishDir)
        if (-not $ShowPlan -and -not (Test-Path (Join-Path $PublishDir 'Lintel.exe'))) { throw "publish did not produce $PublishDir\Lintel.exe" }
    }

    if ($Zip) {
        $zipName = 'Lintel-win-x64.zip'   # version-less: releases/latest/download/<asset> works without the API
        Write-Host "==> release assets in $ReleaseDir" -ForegroundColor Cyan
        if (-not $ShowPlan) {
            New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
            $stage = Join-Path $Artifacts "zip-stage"
            if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
            New-Item -ItemType Directory -Force -Path $stage | Out-Null
            Copy-Item (Join-Path $PublishDir 'Lintel.exe') $stage
            Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $stage 'LICENSE')
            @"
Lintel $Version

Run Lintel.exe: a tray app, no window. Nothing else to install, no .NET needed.
Settings and data live in %LOCALAPPDATA%\Lintel (delete that folder to remove them).
To install properly (Start menu, Apps entry, upgrades), use install.ps1 from the release page.
Licence: GPL-3.0-or-later (LICENSE). Project: https://github.com/nio808/Lintel
"@ | Set-Content -Encoding ASCII (Join-Path $stage 'README.txt')
            $zipPath = Join-Path $ReleaseDir $zipName
            if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
            Remove-Item -Recurse -Force $stage
            Copy-Item (Join-Path $Root 'install.ps1') (Join-Path $ReleaseDir 'install.ps1') -Force
            Write-Sha256Sums $ReleaseDir @($zipName, 'install.ps1')
            Get-Content (Join-Path $ReleaseDir 'SHA256SUMS.txt') | ForEach-Object { Write-Host "    $_" }
        }
    }
    Write-Host 'Done.' -ForegroundColor Green
    exit 0
} catch {
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    Pop-Location
}
