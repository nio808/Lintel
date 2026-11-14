# Pester-free checks for install.ps1 / build.ps1. Runs on Windows PowerShell 5.1 and PowerShell 7.
#   powershell -NoProfile -File tests\installer\Install.Tests.ps1     (exit code = number of failures)
# Installs a fake zip into a temp folder with -NoLaunch, then uninstalls it again. If Lintel is already installed or
# running on this PC, those real-install checks are skipped so nothing of yours is touched.
$ErrorActionPreference = 'Stop'
$Root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$script:fail = 0
$script:n = 0
function Check([string]$Name, [scriptblock]$Body) {
    $script:n++
    try { if (& $Body) { Write-Host "ok   $Name" } else { Write-Host "FAIL $Name" -ForegroundColor Red; $script:fail++ } }
    catch { Write-Host "FAIL $Name -> $($_.Exception.Message)" -ForegroundColor Red; $script:fail++ }
}
function Run-Script([string[]]$ScriptArgs) {
    $ErrorActionPreference = "Continue"
    $exe = (Get-Process -Id $PID).Path
    $out = & $exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'install.ps1') @ScriptArgs 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Out = $out }
}

# --- syntax
foreach ($f in 'install.ps1', 'build.ps1', 'tests\installer\Install.Tests.ps1') {
    Check "parses: $f" {
        $errs = $null; [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $Root $f), [ref]$null, [ref]$errs)
        $errs.Count -eq 0
    }
}
Check 'install.ps1 is pure ASCII (safe for -File on 5.1)' { -not ([IO.File]::ReadAllBytes((Join-Path $Root 'install.ps1')) | Where-Object { $_ -gt 127 }) }

# --- pure functions
. (Join-Path $Root 'install.ps1')
$h = 'a' * 64; $H = 'B' * 64
$sums = "$h  Lintel-win-x64.zip`n$H *install.ps1`r`nnot a checksum line`n"
Check 'checksum: finds text-mode entry'   { (Get-ChecksumFromText $sums 'Lintel-win-x64.zip') -eq $h }
Check 'checksum: binary marker + case'    { (Get-ChecksumFromText $sums 'INSTALL.ps1') -eq $H.ToLower() }
Check 'checksum: unknown file -> null'    { $null -eq (Get-ChecksumFromText $sums 'other.exe') }
Check 'checksum: short hash rejected'     { $null -eq (Get-ChecksumFromText "abc123  x.exe" 'x.exe') }
Check 'url: latest'     { (Get-AssetUrl 'o/r' '' 'a.zip') -eq 'https://github.com/o/r/releases/latest/download/a.zip' }
Check 'url: version'    { (Get-AssetUrl 'o/r' '1.2.3' 'a.zip') -eq 'https://github.com/o/r/releases/download/v1.2.3/a.zip' }
Check 'url: v-version'  { (Get-AssetUrl 'o/r' 'v1.2.3' 'a.zip') -eq 'https://github.com/o/r/releases/download/v1.2.3/a.zip' }
Check 'uninstall script parses and defines Stop-Lintel' {
    $errs = $null; [void][Management.Automation.Language.Parser]::ParseInput((Get-UninstallScript), [ref]$null, [ref]$errs)
    $errs.Count -eq 0 -and (Get-UninstallScript) -match 'function Stop-Lintel'
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("un-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $f = Join-Path $tmp 'a.bin'; [IO.File]::WriteAllBytes($f, [byte[]](1, 2, 3))
    $real = (Get-FileHash -Algorithm SHA256 $f).Hash
    Check 'hash: match (case-insensitive)' { Test-FileHash $f $real.ToLower() }
    Check 'hash: mismatch rejected'        { -not (Test-FileHash $f ('0' * 64)) }
    Check 'hash: missing expected rejected' { -not (Test-FileHash $f $null) }
} finally { Remove-Item -Recurse -Force $tmp }

# --- -DryRun changes nothing
$uk = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lintel'
$runBefore = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' | Out-String)
$ukBefore = Test-Path $uk
$tempBefore = @(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'Lintel-*' -ErrorAction SilentlyContinue).Count
$r = Run-Script @('-DryRun', '-Source', 'C:\nope\Lintel-win-x64.zip', '-InstallDir', 'C:\Temp\UN', '-Startup')
Check 'dryrun -Source: exit 0, plans install'   { $r.Code -eq 0 -and $r.Out -match 'WOULD: install from local C:\\nope' -and $r.Out -match 'WOULD: set Run value Lintel' }
Check 'dryrun: WOULD launch'                    { $r.Out -match 'WOULD: launch' }
$r2 = Run-Script @('-DryRun', '-NoLaunch')
Check 'dryrun -NoLaunch: no launch line'        { $r2.Out -notmatch 'WOULD: launch' }
Check 'dryrun latest: version-less download URLs' { $r2.Code -eq 0 -and $r2.Out -match 'releases/latest/download/Lintel-win-x64\.zip' -and $r2.Out -match 'releases/latest/download/SHA256SUMS\.txt' -and $r2.Out -match 'verify SHA-256' }
$r4 = Run-Script @('-DryRun', '-Version', '1.2.3')
Check 'dryrun -Version: tag url'                { $r4.Out -match 'releases/download/v1\.2\.3/Lintel-win-x64\.zip' }
$r5 = Run-Script @('-DryRun', '-Uninstall', '-Purge')
Check 'dryrun uninstall -Purge'                 { $r5.Code -eq 0 -and $r5.Out -match 'WOULD: quit Lintel.*%LOCALAPPDATA%\\Lintel' }
$r6 = Run-Script @('-Source', (Join-Path ([IO.Path]::GetTempPath()) 'does-not-exist.zip'))
Check 'missing -Source: fails, nonzero'         { $r6.Code -ne 0 -and $r6.Out -match 'Source not found' }
$r7 = Run-Script @('-Version', 'not-a-version', '-DryRun')
Check 'bad -Version rejected'                   { $r7.Code -ne 0 -and $r7.Out -match 'not a version' }
Check 'dryrun left registry and temp untouched' {
    ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' | Out-String) -eq $runBefore) -and ((Test-Path $uk) -eq $ukBefore) -and
    (@(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'Lintel-*' -ErrorAction SilentlyContinue).Count -eq $tempBefore)
}

# --- release path with mocked network: checksum verification happens before anything is installed
function Invoke-MockedInstall([string]$SumsHash, [string]$Dir) {
    $global:MockHash = $SumsHash
    function global:Invoke-WebRequest { param($Uri, $OutFile, $Headers, [switch]$UseBasicParsing)
        if ($Uri -like '*SHA256SUMS.txt') { [IO.File]::WriteAllText($OutFile, $(if ($global:MockHash) { "$($global:MockHash)  Lintel-win-x64.zip`n" } else { "" })) }
        else { [IO.File]::WriteAllBytes($OutFile, [byte[]](7, 7, 7)) } }
    $err = $null
    try { Invoke-LintelInstall -InstallDir $Dir -NoLaunch *>$null } catch { $err = $_.Exception.Message }
    Remove-Item function:global:Invoke-WebRequest -ErrorAction SilentlyContinue
    return $err
}
$mdir = Join-Path ([IO.Path]::GetTempPath()) ("un-mock-" + [guid]::NewGuid().ToString('N'))
$e1 = Invoke-MockedInstall ('0' * 64) $mdir
Check 'download: wrong hash -> aborts, nothing installed' { $e1 -match 'SHA-256 mismatch' -and -not (Test-Path $mdir) }
$e2 = Invoke-MockedInstall '' $mdir
Check 'download: no checksum listed -> aborts'            { $e2 -match 'does not list' -and -not (Test-Path $mdir) }

# --- real install -> upgrade -> uninstall with a fake zip into a temp folder (skipped if Lintel is on this PC)
$mutexHeld = $false
try { $mx = [Threading.Mutex]::OpenExisting('Local\Lintel.SingleInstance'); $mx.Dispose(); $mutexHeld = $true } catch { }
if ($ukBefore -or $mutexHeld -or (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\Lintel'))) {
    Write-Host 'skip real-install checks: Lintel is installed or running on this PC' -ForegroundColor Yellow
} else {
    $work = Join-Path ([IO.Path]::GetTempPath()) ("un-inst-" + [guid]::NewGuid().ToString('N'))
    $dir = Join-Path $work 'app'; $stage = Join-Path $work 'stage'
    $lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lintel.lnk'
    $dataDir = Join-Path $env:LOCALAPPDATA 'Lintel'
    $dataExisted = Test-Path $dataDir
    New-Item -ItemType Directory $stage | Out-Null
    try {
        Set-Content $stage\Lintel.exe 'fake'; Set-Content $stage\LICENSE 'x'; Set-Content $stage\README.txt 'x'
        $zip = Join-Path $work 'Lintel-win-x64.zip'
        Compress-Archive -Path "$stage\*" -DestinationPath $zip
        $good = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower()

        [IO.File]::WriteAllText((Join-Path $work 'SHA256SUMS.txt'), "$('0' * 64)  Lintel-win-x64.zip`n")
        $rb = Run-Script @('-Source', $zip, '-InstallDir', $dir, '-NoLaunch')
        Check 'install: tampered SHA256SUMS next to -Source -> refused' { $rb.Code -ne 0 -and $rb.Out -match 'mismatch' -and -not (Test-Path $dir) -and -not (Test-Path $uk) }

        [IO.File]::WriteAllText((Join-Path $work 'SHA256SUMS.txt'), "$good  Lintel-win-x64.zip`n")
        # Regression: -Startup once recreated the Run key and wiped every other app's startup entry.
        Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name LintelTestSentinel -Value 'keep-me'
        $ri = Run-Script @('-Source', $zip, '-InstallDir', $dir, '-NoLaunch', '-Startup')
        Check 'install: -Startup keeps other apps'' startup entries' { (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').LintelTestSentinel -eq 'keep-me' }
        Check 'install: exit 0, verified, files in place' { $ri.Code -eq 0 -and $ri.Out -match 'SHA-256 verified' -and (Test-Path "$dir\Lintel.exe") -and (Test-Path "$dir\uninstall.ps1") -and (Test-Path "$dir\LICENSE") }
        Check 'install: Start-menu shortcut' { Test-Path $lnk }
        Check 'install: Uninstall entry has the fields' {
            $k = Get-ItemProperty $uk
            $k.DisplayName -eq 'Lintel' -and $k.Publisher -eq 'nio808' -and $k.InstallLocation -eq $dir -and $k.NoModify -eq 1 -and $k.NoRepair -eq 1 -and
            $k.DisplayIcon -eq "$dir\Lintel.exe" -and $k.UninstallString -like '*uninstall.ps1*' -and $k.QuietUninstallString -like '*-WindowStyle Hidden*' -and $k.EstimatedSize -gt 0
        }
        Check 'install: -Startup wrote Run value Lintel' { (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').Lintel -eq "`"$dir\Lintel.exe`" --autostart" }

        Set-Content "$dir\stale.txt" 'old'
        $ru = Run-Script @('-Source', $zip, '-InstallDir', $dir, '-NoLaunch')
        Check 'upgrade: in place, still exactly one Uninstall entry' {
            $ru.Code -eq 0 -and $ru.Out -match 'Upgraded' -and @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object { $_.PSChildName -eq 'Lintel' }).Count -eq 1
        }

        New-Item -ItemType Directory $dataDir -Force | Out-Null
        $rx = Run-Script @('-Uninstall', '-InstallDir', $dir)
        Check 'uninstall: removes dir, shortcut, entry, Run value; keeps data' {
            $rx.Code -eq 0 -and -not (Test-Path $dir) -and -not (Test-Path $lnk) -and -not (Test-Path $uk) -and
            -not (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').Lintel -and (Test-Path $dataDir)
        }
        if (-not $dataExisted) {
            $null = Run-Script @('-Source', $zip, '-InstallDir', $dir, '-NoLaunch')
            $rp = Run-Script @('-Uninstall', '-Purge', '-InstallDir', $dir)
            Check 'uninstall -Purge: also removes %LOCALAPPDATA%\Lintel' { $rp.Code -eq 0 -and -not (Test-Path $dataDir) -and -not (Test-Path $dir) }
        }
        $rn = Run-Script @('-Uninstall', '-InstallDir', $dir)
        Check 'uninstall when absent: friendly, exit 0' { $rn.Code -eq 0 -and $rn.Out -match 'not installed' }
    } finally {
        # leave no trace even if a check threw
        Remove-Item $lnk -Force -ErrorAction SilentlyContinue
        Remove-Item $uk -Recurse -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Lintel -ErrorAction SilentlyContinue
        Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name LintelTestSentinel -ErrorAction SilentlyContinue
        if (-not $dataExisted) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- build.ps1 SDK resolution (mocked) + bootstrap plan
. (Join-Path $Root 'build.ps1')
$fakeRoot = Join-Path ([IO.Path]::GetTempPath()) ("un-root-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory (Join-Path $fakeRoot '.dotnet') | Out-Null
try {
    $v10 = { param($p) , @([version]'10.0.404') }
    $v8  = { param($p) , @([version]'8.0.100') }
    $none = { $null }
    $pathDn = { 'C:\fake\dotnet.exe' }
    $boot = { param($root) 'BOOTSTRAPPED' }
    Check 'sdk: PATH dotnet 10 wins' { (Resolve-Dotnet $fakeRoot $pathDn $v10 $boot).Source -eq 'PATH' }
    Check 'sdk: PATH dotnet 8 ignored -> bootstrap' { (Resolve-Dotnet $fakeRoot $pathDn $v8 $boot).Source -eq 'bootstrap' }
    Check 'sdk: no dotnet -> bootstrap' { (Resolve-Dotnet $fakeRoot $none $v10 $boot).Path -eq 'BOOTSTRAPPED' }
    Set-Content (Join-Path $fakeRoot '.dotnet\dotnet.exe') 'x'
    Check 'sdk: local .dotnet used' { $s = Resolve-Dotnet $fakeRoot $none $v10 $boot; $s.Source -eq 'local' -and $s.Path -like '*\.dotnet\dotnet.exe' }
    Check 'sdk: PATH preferred over local' { (Resolve-Dotnet $fakeRoot $pathDn $v10 $boot).Source -eq 'PATH' }
    Check 'sdk: stale local -> bootstrap' { (Resolve-Dotnet $fakeRoot $none $v8 $boot).Source -eq 'bootstrap' }
} finally { Remove-Item -Recurse -Force $fakeRoot }
Check 'bootstrap plan: exact dotnet-install call' {
    $p = Get-BootstrapPlan 'D:\R'
    $p.Url -eq 'https://dot.net/v1/dotnet-install.ps1' -and ($p.Primary -join ' ') -eq '-JSonFile D:\R\global.json -InstallDir D:\R\.dotnet -NoPath' -and ($p.Fallback -join ' ') -eq '-Channel 10.0 -InstallDir D:\R\.dotnet -NoPath'
}
Check 'Test-Sdk10 boundaries' { (Test-Sdk10 @([version]'10.0.100')) -and -not (Test-Sdk10 @([version]'10.0.99')) -and -not (Test-Sdk10 @([version]'9.0.300')) }
Check 'default version from props' { (Get-DefaultVersion $Root) -match '^\d+\.\d+\.\d+' }
Check 'sha256sums format' {
    $d = Join-Path ([IO.Path]::GetTempPath()) ("un-sum-" + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $d | Out-Null
    try { [IO.File]::WriteAllText((Join-Path $d 'a.zip'), 'hello'); Write-Sha256Sums $d @('a.zip')
          (Get-Content -Raw (Join-Path $d 'SHA256SUMS.txt')) -eq "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824  a.zip`n" } finally { Remove-Item -Recurse -Force $d }
}

Write-Host "`n$($script:n - $script:fail)/$($script:n) passed"
exit $script:fail
