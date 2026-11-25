<# : batch portion - this file is a .cmd that runs the PowerShell body below
@echo off
setlocal
title Petrocon - Revit MCP Installer
set "PETROCON_SCRIPT=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ([IO.File]::ReadAllText($env:PETROCON_SCRIPT))"
echo.
echo Finished. Scroll up to read the results, then press any key to close this window.
pause >nul
exit /b
#>

# ---------------------------------------------------------------------------
# RevitMCP.cmd - Petrocon one-click installer for Model MCP (Revit 2022-2027)
# ONE file for every PC: new PCs, half-installed PCs and already-working PCs.
#   - uv (installed if missing, no admin), office firewall certificates
#   - Revit add-in  : %APPDATA%\Autodesk\ApplicationPlugins\RevitModelMcp.bundle
#   - MCP server    : installed once as a uv tool (no install at Claude start-up)
#   - Claude        : all three settings locations, Developer Mode
# Windows PowerShell 5.1, ASCII only, normal user. Everything changed is backed up.
# Log: %TEMP%\RevitMCP-install-<date>-<time>.log
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
Remove-TypeData System.Array -ErrorAction SilentlyContinue
$Build          = '2026-11-23'
$FallbackVer    = '2.1.0'
$ServerName     = 'revit-model-mcp'
$RepoUrl        = 'https://github.com/sharafutdinovdi/revit-model-mcp'
$UvZipUrl       = 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip'
$PluginsDir     = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$BundleDir      = Join-Path $PluginsDir 'RevitModelMcp.bundle'
$LocalBin       = Join-Path $env:USERPROFILE '.local\bin'
$Stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$Log    = Join-Path $env:TEMP "RevitMCP-install-$Stamp.log"
$Work   = Join-Path $env:TEMP "RevitMCP-$Stamp"
$Backup = Join-Path $env:LOCALAPPDATA "Petrocon\RevitMCP-backup\$Stamp"
$script:StepNo = 0
$StepTotal = 8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

function Write-LogLine([string]$Text) { Add-Content -LiteralPath $Log -Value ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Text) }
function Step([string]$Title) {
    $script:StepNo++
    Write-Host ''
    Write-Host ("==> {0}/{1}  {2}" -f $script:StepNo, $StepTotal, $Title) -ForegroundColor Cyan
    Write-LogLine ("==> {0}/{1} {2}" -f $script:StepNo, $StepTotal, $Title)
}
function Ok([string]$Msg)   { Write-Host "  [OK]   $Msg" -ForegroundColor Green;  Write-LogLine "[OK] $Msg" }
function Info([string]$Msg) { Write-Host "         $Msg" -ForegroundColor Gray;   Write-LogLine "     $Msg" }
function Warn([string]$Msg) { Write-Host "  [WARN] $Msg" -ForegroundColor Yellow; Write-LogLine "[WARN] $Msg" }
function Fail([string]$Msg) {
    Write-Host "  [FAIL] $Msg" -ForegroundColor Red
    Write-LogLine "[FAIL] $Msg"
    Write-Host ''
    Write-Host "Installation stopped. Log file: $Log" -ForegroundColor Red
    Write-Host 'The folder with the log will now open (file is selected). Send it to your BIM/IT support.' -ForegroundColor Red
    try { Start-Process explorer.exe "/select,`"$Log`"" } catch {}
    exit 1
}
# Run native tools so that stderr warnings are logged, not fatal
function Invoke-Native([string]$Exe, [string[]]$ArgList) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & $Exe @ArgList 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $old
    foreach ($l in $out) { if ($l) { Add-Content -LiteralPath $Log -Value "        $l" } }
    $script:LastNativeOutput = ($out -join "`n")
    return $code
}
function Backup-File([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Copy-Item -LiteralPath $Path -Destination "$Path.bak-$Stamp" -Force
        Write-LogLine "     backup: $Path.bak-$Stamp"
    }
}
function Move-ToBackup([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $dest = Join-Path $Backup $Label
    New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
    Move-Item -LiteralPath $Path -Destination $dest -Force
    Info "moved to backup: $Path -> $dest"
}
function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# ----------------------------- Banner --------------------------------------
Write-Host ''
Write-Host '  =====================================================================' -ForegroundColor Cyan
Write-Host '   Petrocon Engineers & Consultants - BIM Department' -ForegroundColor Cyan
Write-Host '   Revit MCP Installer  (Model MCP for Revit 2022-2027)' -ForegroundColor Cyan
Write-Host '  =====================================================================' -ForegroundColor Cyan
Write-Host '   A big THANK YOU to:' -ForegroundColor White
Write-Host '    - Rahid Mulla (BIM Lead), for building, testing and rolling this out' -ForegroundColor White
Write-Host '    - Imthiaz Ahmed (Head of the Department), for leading the way' -ForegroundColor White
Write-Host '    - Petrocon management, for backing AI in our everyday engineering work' -ForegroundColor White
Write-Host '    - ...and the samosas, which powered every late-evening test run.' -ForegroundColor Yellow
Write-Host ''
Write-Host '   - Every clash caught on screen, not on site? BIM had your back.' -ForegroundColor Green
Write-Host '   - Every discipline brings a piece. BIM makes it one picture.' -ForegroundColor Green
Write-Host '   - BIM never stands above anyone. It just stands UPRIGHT,' -ForegroundColor Green
Write-Host '     always. (Level 0, plumb, no warnings.)' -ForegroundColor Green
Write-Host '  =====================================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Press Enter to start (BIM stays upright, the installer gets to work): ' -ForegroundColor Yellow -NoNewline
[void](Read-Host)
Write-LogLine "RevitMCP installer (build $Build) started on $env:COMPUTERNAME as $env:USERNAME"
New-Item -ItemType Directory -Path $Work -Force | Out-Null

# ----------------------------- 1. Checks -----------------------------------
Step 'Checking this computer'
$os = Get-CimInstance Win32_OperatingSystem
if (-not [Environment]::Is64BitOperatingSystem) { Fail "$($os.Caption) is 32-bit. Revit MCP needs 64-bit Windows." }
Ok "$($os.Caption) (64-bit)"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'Running as administrator. Close this window and double-click the file normally (not "Run as administrator").'
}
Ok "Running as normal user: $env:USERNAME"

$sac = $null
try { $sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction Stop).VerifiedAndReputablePolicyState } catch {}
switch ("$sac") {
    '1' { Fail 'Smart App Control is ON. It blocks the Revit add-in DLL. Ask IT to turn it off, then run this again.' }
    '2' { Warn 'Smart App Control is in Evaluation mode. If Windows switches it ON, the Revit add-in will be blocked.' }
    '0' { Ok 'Smart App Control: Off' }
    default { Ok 'Smart App Control: not present on this Windows' }
}

$revitYears = @()
foreach ($y in 2022..2027) {
    if (Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $y\Revit.exe")) { $revitYears += $y }
}
if ($revitYears.Count -eq 0) { Fail "Revit 2022-2027 was not found in $env:ProgramFiles\Autodesk." }
Ok ("Revit found: " + ($revitYears -join ', '))

$claudeSeen = (Test-Path "$env:LOCALAPPDATA\AnthropicClaude") -or (Test-Path "$env:APPDATA\Claude") -or
              (Test-Path "$env:LOCALAPPDATA\Claude-Data") -or
              @(Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue).Count -gt 0
if ($claudeSeen) { Ok 'Claude Desktop found' } else { Warn 'Claude Desktop not found. Install it from claude.ai/download; the settings will be ready for it.' }

foreach ($u in @('https://pypi.org/simple/revit-model-mcp/', $RepoUrl)) {
    try { $null = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 25; Ok "Reachable: $u" }
    catch { Fail "Cannot reach $u : $($_.Exception.Message)" }
}

# ----------------------------- 2. Certificates -----------------------------
Step 'Office firewall: telling uv to use Windows certificates'
$envWanted = [ordered]@{ 'UV_SYSTEM_CERTS' = 'true'; 'UV_NATIVE_TLS' = 'true'; 'UV_PYTHON' = '3.12' }
foreach ($k in $envWanted.Keys) {
    $before = [Environment]::GetEnvironmentVariable($k, 'User')
    Write-LogLine "     $k user value before: '$before'"
    [Environment]::SetEnvironmentVariable($k, $envWanted[$k], 'User')
    Set-Item -Path "Env:$k" -Value $envWanted[$k]
    Ok "$k = $($envWanted[$k])"
}

# ----------------------------- 3. Close apps -------------------------------
Step 'Closing Revit and Claude'
$rv = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
$cl = @(Get-Process -Name 'claude' -ErrorAction SilentlyContinue)
if ($rv.Count -or $cl.Count) {
    if ($rv.Count) { Write-Host '  Revit is open: SAVE YOUR MODELS NOW.' -ForegroundColor Yellow }
    if ($cl.Count) { Write-Host '  Claude is open: it will be closed; you reopen it at the end.' -ForegroundColor Yellow }
    Write-Host '  Press Enter when ready, and the installer will close them: ' -ForegroundColor Yellow -NoNewline
    [void](Read-Host)
    if ($rv.Count) {
        Get-Process -Name 'Revit' -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.CloseMainWindow() }
        Start-Sleep -Seconds 10
        if (@(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue).Count) { Fail 'Revit is still open (maybe a "Save changes?" box). Close Revit yourself, then run this again.' }
    }
    Get-Process -Name 'claude' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}
Get-Process -Name 'uvx', 'uv' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
try {
    Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction Stop |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath -match '\\uv\\' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
} catch {}
Ok 'Revit and Claude are closed'

# ----------------------------- 4. uv ---------------------------------------
Step 'uv (Python package runner)'
$uv = $null; $uvx = $null
foreach ($cand in @((Join-Path $LocalBin 'uv.exe'), (Join-Path $env:USERPROFILE '.cargo\bin\uv.exe'))) {
    if (Test-Path -LiteralPath $cand) { $uv = $cand; break }
}
if (-not $uv) { $c = Get-Command uv.exe -ErrorAction SilentlyContinue; if ($c) { $uv = $c.Source } }
if ($uv) {
    Ok "uv already installed: $uv"
} else {
    Info 'uv not found - installing it now (no admin needed)'
    $uvZip = Join-Path $Work 'uv.zip'
    try {
        Invoke-WebRequest -Uri $UvZipUrl -OutFile $uvZip -UseBasicParsing -TimeoutSec 300
        $uvSha = (Invoke-WebRequest -Uri "$UvZipUrl.sha256" -UseBasicParsing -TimeoutSec 60).Content
        if ($uvSha -is [byte[]]) { $uvSha = [Text.Encoding]::ASCII.GetString($uvSha) }
    } catch { Fail "Could not download uv from GitHub: $($_.Exception.Message)" }
    $exp = ("$uvSha".Trim() -split '\s+')[0].ToLowerInvariant()
    $act = Get-Sha $uvZip
    Info "uv SHA-256 expected $exp / actual $act"
    if ($exp -ne $act) { Fail 'uv download does not match its published checksum. Nothing was installed.' }
    $uvTmp = Join-Path $Work 'uv'
    Expand-Archive -LiteralPath $uvZip -DestinationPath $uvTmp -Force
    New-Item -ItemType Directory -Path $LocalBin -Force | Out-Null
    foreach ($exe in @('uv.exe', 'uvx.exe', 'uvw.exe')) {
        $src = Get-ChildItem -LiteralPath $uvTmp -Recurse -Filter $exe -File | Select-Object -First 1
        if ($src) { Copy-Item -LiteralPath $src.FullName -Destination (Join-Path $LocalBin $exe) -Force }
    }
    $uv = Join-Path $LocalBin 'uv.exe'
    if (-not (Test-Path -LiteralPath $uv)) { Fail 'uv.exe was not found in the downloaded package.' }
    Get-ChildItem -LiteralPath $LocalBin -Filter 'uv*.exe' | Unblock-File -ErrorAction SilentlyContinue
    Ok "uv installed: $uv"
}
$uvx = Join-Path (Split-Path $uv) 'uvx.exe'
# PATH: make sure the uv folder and the uv tools folder are on the user PATH
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User'); if (-not $userPath) { $userPath = '' }
foreach ($dir in (@((Split-Path $uv), $LocalBin) | Select-Object -Unique)) {
    if (($userPath -split ';') -notcontains $dir) {
        $userPath = ($userPath.TrimEnd(';') + ';' + $dir).TrimStart(';')
        [Environment]::SetEnvironmentVariable('Path', $userPath, 'User')
        Info "added to user PATH: $dir"
    }
    if (($env:Path -split ';') -notcontains $dir) { $env:Path = "$env:Path;$dir" }
}
[void](Invoke-Native $uv @('--version')); Info $script:LastNativeOutput
[void](Invoke-Native $uv @('python', 'uninstall', '3.15'))   # 3.15 breaks pyyaml; harmless if absent

# ----------------------------- 5. Revit add-in -----------------------------
Step 'Revit add-in (Model MCP)'
$ver = $null
try { $ver = (Invoke-RestMethod -Uri 'https://pypi.org/pypi/revit-model-mcp/json' -TimeoutSec 30).info.version } catch { Warn "Could not read the latest version from PyPI: $($_.Exception.Message)" }
if ($ver -notmatch '^\d+\.\d+\.\d+$') { $ver = $FallbackVer; Warn "Using the tested version $ver" }
$tag = "v$ver"
$sumsFile = Join-Path $Work 'SHA256SUMS.txt'
$bundleZipName = "revit-model-mcp-$ver-managed.bundle.zip"
try { Invoke-WebRequest -Uri "$RepoUrl/releases/download/$tag/SHA256SUMS.txt" -OutFile $sumsFile -UseBasicParsing -TimeoutSec 60 }
catch {
    if ($ver -ne $FallbackVer) {
        Warn "Release $tag has no checksum file yet; using the tested version $FallbackVer"
        $ver = $FallbackVer; $tag = "v$ver"; $bundleZipName = "revit-model-mcp-$ver-managed.bundle.zip"
        try { Invoke-WebRequest -Uri "$RepoUrl/releases/download/$tag/SHA256SUMS.txt" -OutFile $sumsFile -UseBasicParsing -TimeoutSec 60 }
        catch { Fail "Could not download the release checksums: $($_.Exception.Message)" }
    } else { Fail "Could not download the release checksums: $($_.Exception.Message)" }
}
$line = @(Get-Content -LiteralPath $sumsFile | Where-Object { $_ -match ("\s\*?" + [regex]::Escape($bundleZipName) + '$') })
if ($line.Count -ne 1) { Fail "Release $tag has no single checksum line for $bundleZipName." }
$expected = ($line[0].Trim() -split '\s+')[0].ToLowerInvariant()
$bundleZip = Join-Path $Work $bundleZipName
try { Invoke-WebRequest -Uri "$RepoUrl/releases/download/$tag/$bundleZipName" -OutFile $bundleZip -UseBasicParsing -TimeoutSec 600 }
catch { Fail "Could not download the add-in: $($_.Exception.Message)" }
$actual = Get-Sha $bundleZip
Info "add-in SHA-256 expected $expected / actual $actual"
if ($expected -ne $actual) { Fail 'The add-in download does not match its published checksum. Nothing was installed.' }
Ok "Downloaded and verified Model MCP add-in $ver"

# Old copies: MSI / manual copies in Revit\Addins\<year> load first and show the wrong version
foreach ($y in 2022..2027) {
    $ad = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$y"
    foreach ($n in @('RevitModelMcp.addin', 'RevitModelMcp')) {
        Move-ToBackup (Join-Path $ad $n) "Addins-$y-$n"
    }
}
Move-ToBackup $BundleDir 'RevitModelMcp.bundle'
New-Item -ItemType Directory -Path $PluginsDir -Force | Out-Null
$bTmp = Join-Path $Work 'bundle'
Expand-Archive -LiteralPath $bundleZip -DestinationPath $bTmp -Force
$srcBundle = Join-Path $bTmp 'RevitModelMcp.bundle'
if (-not (Test-Path -LiteralPath (Join-Path $srcBundle 'PackageContents.xml'))) { Fail 'The add-in package has no RevitModelMcp.bundle\PackageContents.xml.' }
Copy-Item -LiteralPath $srcBundle -Destination $PluginsDir -Recurse -Force
Get-ChildItem -LiteralPath $BundleDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
foreach ($y in $revitYears) {
    if (Test-Path (Join-Path $BundleDir "Contents\$y\RevitModelMcp.addin")) { Ok "Add-in ready for Revit $y" } else { Warn "This package has no add-in for Revit $y" }
}
Ok "Add-in installed: $BundleDir"

# ----------------------------- 6. Server -----------------------------------
Step 'MCP server (installed once, permanently) and handshake test'
$ServerSpec = "$ServerName==$ver"
[void](Invoke-Native $uv @('cache', 'clean', 'pywin32'))
$useTool = $false; $toolExe = $null
for ($t = 1; $t -le 3 -and -not $useTool; $t++) {
    Info "uv tool install $ServerSpec  (attempt $t of 3, first time can take a few minutes)"
    $code = Invoke-Native $uv @('tool', 'install', '--force', '--python', '3.12', $ServerSpec)
    if ($code -eq 0) {
        $bin = $null
        $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { $bin = (& $uv tool dir --bin 2>$null | Select-Object -First 1) } catch {}
        $ErrorActionPreference = $old
        if (-not $bin) { $bin = $LocalBin }
        $cand = Join-Path ("$bin".Trim()) "$ServerName.exe"
        if (Test-Path -LiteralPath $cand) { $toolExe = $cand; $useTool = $true; break }
        Warn "Installed, but $cand was not found."
        break
    }
    if ($script:LastNativeOutput -match 'UnknownIssuer|certificate') { Fail 'Certificate error even with Windows certificates. Windows does not trust the office firewall certificate on this PC: ask IT to install it.' }
    Warn "uv tool install failed (exit code $code). Waiting 15 s, cleaning the cache and retrying."
    Start-Sleep -Seconds 15
    [void](Invoke-Native $uv @('cache', 'clean', 'pywin32'))
}
if ($useTool) { Ok "Server installed: $toolExe" } else { Warn 'Permanent install did not work; Claude will use uvx with the pinned version.' }

function Read-Reply($Proc, [int]$Id, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($Proc.HasExited) { return $null }
        $task = $Proc.StandardOutput.ReadLineAsync()
        while (-not $task.Wait(500)) { if ((Get-Date) -ge $deadline -or $Proc.HasExited) { return $null } }
        $l = $task.Result
        if ($null -eq $l) { return $null }
        if (-not $l.Trim().StartsWith('{')) { continue }
        try { $msg = $l | ConvertFrom-Json } catch { continue }
        if ($msg.PSObject.Properties['id'] -and "$($msg.id)" -eq "$Id") { return $msg }
    }
    return $null
}
function Test-Handshake([string]$Exe, [string[]]$ArgList) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ((@($ArgList) | Where-Object { $_ } | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['REVIT_MCP_HOST'] = 'local'
    $psi.EnvironmentVariables['REVIT_MCP_NO_UPDATE_CHECK'] = '1'
    Info "running: $Exe $($psi.Arguments)"
    $res = @{ Ok = $false; Tools = 0; Name = ''; Err = '' }
    try {
        $p = [System.Diagnostics.Process]::Start($psi)
        $errTask = $p.StandardError.ReadToEndAsync()
        $p.StandardInput.NewLine = "`n"
        $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"petrocon-installer","version":"1"}}}')
        $p.StandardInput.Flush()
        $r0 = Read-Reply $p 0 420
        if ($r0 -and $r0.result) {
            $res.Name = "$($r0.result.serverInfo.name) $($r0.result.serverInfo.version)"
            $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
            $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}')
            $p.StandardInput.Flush()
            $r1 = Read-Reply $p 1 60
            if ($r1 -and $r1.result) { $res.Ok = $true; $res.Tools = @($r1.result.tools).Count }
        }
        try { $p.StandardInput.Close() } catch {}
        if (-not $p.WaitForExit(5000)) { try { $p.Kill() } catch {} }
        if ($errTask.Wait(5000)) { $res.Err = $errTask.Result }
    } catch { $res.Err = "$_" }
    foreach ($l in ("$($res.Err)" -split "`r?`n")) { if ($l) { Add-Content -LiteralPath $Log -Value "        stderr: $l" } }
    return $res
}
function Invoke-Tests([string]$Exe, [string[]]$ArgList) {
    for ($try = 1; $try -le 3; $try++) {
        Info "handshake attempt $try of 3"
        $r = Test-Handshake $Exe $ArgList
        if ($r.Ok) { Ok "Server answered: $($r.Name), $($r.Tools) tools"; return $true }
        if ($r.Err -match 'os error 32') { Warn 'A file is locked (os error 32). Waiting 15 s and cleaning the cache.'; Start-Sleep -Seconds 15; [void](Invoke-Native $uv @('cache', 'clean', 'pywin32')); continue }
        if ($r.Err -match 'UnknownIssuer|certificate') { Fail 'Certificate error even with Windows certificates. Ask IT to install the office firewall certificate on this PC.' }
        Warn 'Handshake did not complete. Details are in the log.'
    }
    return $false
}
$passed = $false
if ($useTool) { $passed = Invoke-Tests $toolExe @() }
if (-not $passed) {
    if ($useTool) { Warn 'The installed server did not answer; trying uvx with the pinned version.' }
    $useTool = $false
    $passed = Invoke-Tests $uvx @($ServerSpec)
}
if (-not $passed) { Fail 'The server did not answer the MCP handshake 3 times. Send the log to Rahid.' }

# ----------------------------- 7. Claude settings --------------------------
Step 'Claude settings (all three locations) and Developer Mode'
$cfgFiles = New-Object System.Collections.Generic.List[string]
$cfgFiles.Add("$env:APPDATA\Claude\claude_desktop_config.json")
Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
    ForEach-Object { $cfgFiles.Add((Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json')) }
Get-ChildItem "$env:LOCALAPPDATA\Claude-Data" -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { $cfgFiles.Add((Join-Path $_.FullName 'claude_desktop_config.json')) }
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$srvEnv = [ordered]@{ 'REVIT_MCP_HOST' = 'local'; 'REVIT_MCP_NO_UPDATE_CHECK' = '1'; 'UV_SYSTEM_CERTS' = 'true'; 'UV_NATIVE_TLS' = 'true'; 'UV_PYTHON' = '3.12' }
$written = 0
foreach ($f in $cfgFiles) {
    $dir = Split-Path $f
    if (-not (Test-Path -LiteralPath $dir)) {
        if ($f -like "$env:APPDATA\Claude\*") { New-Item -ItemType Directory -Path $dir -Force | Out-Null } else { continue }
    }
    $cfg = [pscustomobject]@{}
    if (Test-Path -LiteralPath $f) {
        try { $cfg = [IO.File]::ReadAllText($f) | ConvertFrom-Json } catch { Warn "Not valid JSON, left unchanged: $f"; continue }
        if ($null -eq $cfg) { $cfg = [pscustomobject]@{} }
        Backup-File $f
    }
    if (-not $cfg.PSObject.Properties['mcpServers']) { $cfg | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([pscustomobject]@{}) }
    $envObj = [pscustomobject]@{}
    if ($cfg.mcpServers.PSObject.Properties[$ServerName] -and $cfg.mcpServers.$ServerName.PSObject.Properties['env']) {
        $envObj = $cfg.mcpServers.$ServerName.env   # keep any extra settings someone added
    }
    foreach ($k in $srvEnv.Keys) {
        if ($envObj.PSObject.Properties[$k]) { $envObj.$k = $srvEnv[$k] } else { $envObj | Add-Member -NotePropertyName $k -NotePropertyValue $srvEnv[$k] }
    }
    if ($useTool) { $entry = [pscustomobject]@{ command = $toolExe; args = @(); env = $envObj } }
    else          { $entry = [pscustomobject]@{ command = $uvx; args = @($ServerSpec); env = $envObj } }
    $action = 'Added to'
    if ($cfg.mcpServers.PSObject.Properties[$ServerName]) { $cfg.mcpServers.$ServerName = $entry; $action = 'Updated' }
    else { $cfg.mcpServers | Add-Member -NotePropertyName $ServerName -NotePropertyValue $entry }
    $json = $cfg | ConvertTo-Json -Depth 32
    try { [void]($json | ConvertFrom-Json) } catch { Warn "New JSON did not validate, left unchanged: $f"; continue }
    [IO.File]::WriteAllText($f, $json, $utf8NoBom)
    $others = @($cfg.mcpServers.PSObject.Properties.Name | Where-Object { $_ -ne $ServerName })
    Ok ("$action $f" + $(if ($others.Count) { "  (kept: " + ($others -join ', ') + ")" } else { '' }))
    $written++
}
if ($written -eq 0) { Fail 'Could not write any Claude settings file.' }
$devFile = "$env:APPDATA\Claude\developer_settings.json"
try {
    $dev = [pscustomobject]@{}
    if (Test-Path -LiteralPath $devFile) { Backup-File $devFile; $dev = [IO.File]::ReadAllText($devFile) | ConvertFrom-Json; if ($null -eq $dev) { $dev = [pscustomobject]@{} } }
    if ($dev.PSObject.Properties['allowDevTools']) { $dev.allowDevTools = $true } else { $dev | Add-Member -NotePropertyName 'allowDevTools' -NotePropertyValue $true }
    [IO.File]::WriteAllText($devFile, ($dev | ConvertTo-Json -Depth 8), $utf8NoBom)
    Ok 'Developer Mode enabled'
} catch { Warn "Could not enable Developer Mode: $_" }
$mcpb = @(Get-ChildItem "$env:APPDATA\Claude\Claude Extensions" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'revit' })
if ($mcpb.Count) { Warn ("A Revit extension (.mcpb) is also installed in Claude: " + ($mcpb.Name -join ', ') + ". Remove it in Claude > Settings > Extensions to avoid two Revit servers.") }

# ----------------------------- 8. Done -------------------------------------
Step 'Finished'
Ok "Revit MCP $ver is installed: add-in + server + Claude settings."
if ($useTool) { Ok 'Claude starts the installed server directly (nothing is installed at start-up).' }
Write-Host ''
Write-Host '  Next steps:' -ForegroundColor White
Write-Host '   1. Open Claude. Settings > Developer should show revit-model-mcp as running.' -ForegroundColor White
Write-Host '   2. Open Revit (choose "Always Load" if asked) and open a model.' -ForegroundColor White
Write-Host "      Add-Ins > Model MCP > About should show $ver." -ForegroundColor White
Write-Host '   3. In a Claude chat on this PC, ask: "What model is open in Revit?" (read-only first).' -ForegroundColor White
Write-Host "   4. To undo: old files are in $Backup and the .bak files listed in the log." -ForegroundColor White
Write-Host ''
Write-Host "  Log file: $Log" -ForegroundColor Cyan
Write-LogLine 'Completed successfully'
