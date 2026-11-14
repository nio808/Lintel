<#
.SYNOPSIS
  Installs, upgrades or removes Lintel (per-user, no admin, no .NET needed).

.EXAMPLE
  irm https://raw.githubusercontent.com/nio808/Lintel/main/install.ps1 | iex

.EXAMPLE
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nio808/Lintel/main/install.ps1))) -Uninstall

  Options: -Version 1.2.3  -Startup  -NoLaunch  -Uninstall  -Purge  -DryRun  -Source <Lintel-win-x64.zip>
#>
param(
    [string]$Version,
    [switch]$Startup,
    [switch]$NoLaunch,
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$DryRun,
    [string]$Source,
    [string]$InstallDir   # testing only; default %LOCALAPPDATA%\Programs\Lintel
)

$Repo = 'nio808/Lintel'
$ZipName = 'Lintel-win-x64.zip'
$SumsName = 'SHA256SUMS.txt'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lintel'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue = 'Lintel'   # same value name as StartupRegistration in the app

function Say([string]$Text) { Write-Host "  $([char]0x2713) " -ForegroundColor Green -NoNewline; Write-Host $Text }
function Plan([string]$Text) { Write-Host "  WOULD: $Text" -ForegroundColor Yellow }

# Create a registry key and any missing parents. Never New-Item -Force: on an existing key it
# recreates it and wipes every value (e.g. other apps' startup entries under Run).
function Confirm-RegistryKey([string]$Path) {
    if (Test-Path $Path) { return }
    Confirm-RegistryKey (Split-Path $Path -Parent)
    New-Item -Path $Path | Out-Null
}

# Parses "<64 hex>  <file>" lines (sha256sum format, optional '*' binary marker). Returns lowercase hash or $null.
function Get-ChecksumFromText([string]$Text, [string]$FileName) {
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -ieq $FileName) {
            return $Matches[1].ToLowerInvariant()
        }
    }
    return $null
}

function Test-FileHash([string]$Path, [string]$Expected) {
    if (-not $Expected) { return $false }
    return ((Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() -eq $Expected.ToLowerInvariant())
}

# Latest -> /releases/latest/download/<asset> (no API call); pinned -> /releases/download/v<ver>/<asset>.
function Get-AssetUrl([string]$Repo, [string]$Version, [string]$Name) {
    if ($Version) { return "https://github.com/$Repo/releases/download/v$($Version.TrimStart('v'))/$Name" }
    return "https://github.com/$Repo/releases/latest/download/$Name"
}

# Asks a running instance to quit (--quit, ~1 s), waits for its mutex to vanish, kills only <Dir>\Lintel.exe as a last resort.
function Stop-Lintel([string]$Dir) {
    $exe = Join-Path $Dir 'Lintel.exe'
    $running = {
        $m = $null
        try { $m = [Threading.Mutex]::OpenExisting('Local\Lintel.SingleInstance') } catch { }
        if ($m) { $m.Dispose(); return $true }
        return $false
    }
    if (-not (& $running)) { return }
    if (Test-Path -LiteralPath $exe) { try { Start-Process -FilePath $exe -ArgumentList '--quit' -Wait } catch { } }
    for ($i = 0; $i -lt 40 -and (& $running); $i++) { Start-Sleep -Milliseconds 250 }
    if (& $running) {
        Get-Process -Name Lintel -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and ($_.Path -ieq $exe) } | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

# Text of <InstallDir>\uninstall.ps1 (also what -Uninstall runs): one source for both.
function Get-UninstallScript {
    $head = @'
param([switch]$Purge, [string]$InstallDir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
'@
    $body = @'
if (-not $InstallDir) { throw 'No install folder given.' }
Stop-Lintel $InstallDir
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lintel.lnk'
Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$run = (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).Lintel
if ($run -and $run -like "*$InstallDir*") {
    Remove-ItemProperty -Path $runKey -Name Lintel -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name Lintel -ErrorAction SilentlyContinue
}
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lintel' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $InstallDir) {
    # still locked (e.g. this very script): finish after we exit
    Start-Process cmd.exe -WindowStyle Hidden -ArgumentList "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q `"$InstallDir`""
}
if ($Purge) { Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'Lintel') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "  $([char]0x2713) " -ForegroundColor Green -NoNewline
Write-Host $(if ($Purge) { 'Lintel removed, including its settings and data.' } else { 'Lintel removed. Settings kept in %LOCALAPPDATA%\Lintel (add -Purge to delete them).' })
'@
    $fn = (Get-Item function:Stop-Lintel).ScriptBlock.ToString()
    return "$head`nfunction Stop-Lintel {$fn}`n$body`n"
}

function Invoke-LintelInstall {
    param([string]$Version, [switch]$Startup, [switch]$NoLaunch, [switch]$Uninstall, [switch]$Purge, [switch]$DryRun, [string]$Source, [string]$InstallDir)
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # 5.1 downloads are ~10x faster without the bar
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Lintel needs 64-bit Windows.' }
    if ([Environment]::OSVersion.Version.Build -lt 19041) { throw 'Lintel needs Windows 10 version 2004 (build 19041) or newer.' }
    if ($Version -and $Version -notmatch '^v?\d+\.\d+\.\d+([-+][0-9A-Za-z.\-+]+)?$') { throw "'$Version' is not a version like 1.2.3." }
    if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\Lintel' }
    $exe = Join-Path $InstallDir 'Lintel.exe'

    # ---- uninstall
    if ($Uninstall) {
        if ($DryRun) { Plan "quit Lintel, delete $InstallDir, its Start-menu shortcut, Uninstall entry and Run value$(if ($Purge) { ', and %LOCALAPPDATA%\Lintel' })"; return }
        if (-not (Test-Path -LiteralPath $InstallDir) -and -not (Test-Path $UninstallKey)) { Say 'Lintel is not installed.'; return }
        & ([scriptblock]::Create((Get-UninstallScript))) -InstallDir $InstallDir -Purge:$Purge
        return
    }

    # ---- get + verify the zip
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('Lintel-' + [guid]::NewGuid().ToString('N'))
    try {
        $zip = Join-Path $tmp $ZipName
        $expected = $null
        if ($Source) {
            if ($DryRun) { Plan "install from local $Source" }
            else {
                if (-not (Test-Path -LiteralPath $Source)) { throw "Source not found: $Source" }
                $zip = (Resolve-Path -LiteralPath $Source).Path
                $sibling = Join-Path (Split-Path -Parent $zip) $SumsName
                if (Test-Path -LiteralPath $sibling) {
                    $expected = Get-ChecksumFromText (Get-Content -Raw -LiteralPath $sibling) (Split-Path -Leaf $zip)
                    if (-not $expected) { throw "$SumsName next to the zip does not list $(Split-Path -Leaf $zip)." }
                } else {
                    Write-Host '  -Source without a SHA256SUMS.txt next to it: checksum not verified.' -ForegroundColor Yellow
                }
            }
        } else {
            $zipUrl = Get-AssetUrl $Repo $Version $ZipName
            $sumsUrl = Get-AssetUrl $Repo $Version $SumsName
            if ($DryRun) { Plan "download $zipUrl and $sumsUrl"; Plan 'verify SHA-256; abort if it does not match' }
            else {
                New-Item -ItemType Directory -Force -Path $tmp | Out-Null
                Write-Host "  Downloading Lintel$(if ($Version) { " $($Version.TrimStart('v'))" }) ..."
                $sumsFile = Join-Path $tmp $SumsName
                Invoke-WebRequest -UseBasicParsing -Uri $zipUrl -OutFile $zip -Headers @{ 'User-Agent' = 'Lintel-Installer' }
                Invoke-WebRequest -UseBasicParsing -Uri $sumsUrl -OutFile $sumsFile -Headers @{ 'User-Agent' = 'Lintel-Installer' }
                $expected = Get-ChecksumFromText (Get-Content -Raw -LiteralPath $sumsFile) $ZipName
                if (-not $expected) { throw "$SumsName does not list $ZipName. Nothing was installed." }
            }
        }
        if (-not $DryRun -and $expected) {
            if (-not (Test-FileHash $zip $expected)) { throw 'SHA-256 mismatch: the download is not what the release published. Nothing was installed.' }
            Say 'SHA-256 verified'
        }

        if ($DryRun) {
            Plan "quit a running Lintel (--quit), extract to $InstallDir, write uninstall.ps1, Start-menu shortcut, Uninstall entry"
            if ($Startup) { Plan "set Run value $RunValue" }
            if (-not $NoLaunch) { Plan 'launch Lintel.exe' }
            return
        }

        # ---- stage, stop, copy (in-place upgrade)
        $stage = Join-Path $tmp 'stage'
        New-Item -ItemType Directory -Force -Path $tmp | Out-Null
        Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
        if (-not (Test-Path -LiteralPath (Join-Path $stage 'Lintel.exe'))) { throw "$ZipName does not contain Lintel.exe." }
        $upgrade = Test-Path -LiteralPath $exe
        Stop-Lintel $InstallDir
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        Copy-Item -Path (Join-Path $stage '*') -Destination $InstallDir -Recurse -Force
        [IO.File]::WriteAllText((Join-Path $InstallDir 'uninstall.ps1'), (Get-UninstallScript), (New-Object Text.UTF8Encoding($true)))
        $ver = ("$(try { (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } catch { '' })" -split '\+')[0]
        if (-not $ver) { $ver = "$Version".TrimStart('v') }
        if (-not $ver) { $ver = 'unknown' }
        Say "$(if ($upgrade) { 'Upgraded' } else { 'Installed' }) Lintel $ver in $InstallDir"

        # ---- shortcut, Uninstall entry, optional startup
        $lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lintel.lnk'
        $sh = New-Object -ComObject WScript.Shell
        $s = $sh.CreateShortcut($lnk)
        $s.TargetPath = $exe; $s.WorkingDirectory = $InstallDir; $s.IconLocation = "$exe,0"; $s.Description = 'Lintel'
        $s.Save()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($s)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($sh)
        $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass"
        $file = "-File `"$(Join-Path $InstallDir 'uninstall.ps1')`""
        $size = [int]((Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
        Confirm-RegistryKey $UninstallKey
        $props = @{
            DisplayName = 'Lintel'; DisplayVersion = $ver; Publisher = 'nio808'; DisplayIcon = $exe; InstallLocation = $InstallDir
            URLInfoAbout = "https://github.com/$Repo"; UninstallString = "$cmd $file"; QuietUninstallString = "$cmd -WindowStyle Hidden $file"
        }
        foreach ($k in $props.Keys) { Set-ItemProperty -Path $UninstallKey -Name $k -Value $props[$k] }
        Set-ItemProperty -Path $UninstallKey -Name EstimatedSize -Value $size -Type DWord
        Set-ItemProperty -Path $UninstallKey -Name NoModify -Value 1 -Type DWord
        Set-ItemProperty -Path $UninstallKey -Name NoRepair -Value 1 -Type DWord
        Say 'Start menu shortcut and Apps entry added'
        if ($Startup) {
            # Never use New-Item -Force here: on an existing key it recreates it and wipes every other app's startup entry.
            Confirm-RegistryKey $RunKey
            Set-ItemProperty -Path $RunKey -Name $RunValue -Value "`"$exe`" --autostart"
            Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name $RunValue -ErrorAction SilentlyContinue
            Say 'Will start with Windows'
        }
        if (-not $NoLaunch) { Start-Process -FilePath $exe; Say 'Lintel is running (look for the tray icon)' }
        Write-Host '  Remove it any time: Windows Settings > Apps > Lintel.' -ForegroundColor DarkGray
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# Dot-sourced (tests): define functions only.
if ($MyInvocation.InvocationName -eq '.') { return }

$code = 0
try {
    Invoke-LintelInstall -Version $Version -Startup:$Startup -NoLaunch:$NoLaunch -Uninstall:$Uninstall -Purge:$Purge -DryRun:$DryRun -Source $Source -InstallDir $InstallDir
} catch {
    Write-Host "  x $($_.Exception.Message)" -ForegroundColor Red
    $code = 1
}
# Never `exit` under `irm | iex`: it would close the user's terminal.
if ($PSCommandPath) { exit $code } elseif ($code -ne 0) { $global:LASTEXITCODE = $code }
