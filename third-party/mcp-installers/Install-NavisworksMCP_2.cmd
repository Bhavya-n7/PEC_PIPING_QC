<# :
@echo off
REM ===================================================================
REM  Navisworks MCP (nwd-mcp + NavisCoord) - one-click installer (version 2)
REM  Double-click this file. Do NOT use "Run as administrator".
REM  The PowerShell code below this header does all the work.
REM ===================================================================
setlocal
set "NAVMCP_SCRIPT=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText($env:NAVMCP_SCRIPT); Invoke-Expression $s"
set "NAVMCP_RC=%errorlevel%"
echo.
echo Finished. Scroll up to read the results, then press any key to close this window.
pause >nul
exit /b %NAVMCP_RC%
#>

# =====================================================================
#  Navisworks MCP - installer (PowerShell part)
#  Installs the two Navisworks MCP servers used at Petrocon:
#   A) nwd-mcp    (Bimwright, Apache-2.0)  github.com/bimwright/nwd-mcp
#      model browsing, properties, search sets, viewpoints, C# scripts
#   B) NavisCoord (HorizunGroup, MIT)      github.com/HorizunGroup/naviscoord-mcp
#      clash coordination: tests, ranking, hotspots, reports
#  Steps: pre-checks -> download official releases (checksums verified)
#  -> Navisworks plug-ins -> servers -> handshake test -> Claude config
# =====================================================================

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

$InstallerVersion = '2'
$Stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$WorkDir = Join-Path $env:TEMP "NavisMCP-install-$Stamp"
$LogFile = Join-Path $env:TEMP "NavisMCP-install-$Stamp.log"
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
try { Start-Transcript -Path $LogFile -Force | Out-Null } catch {}

$Results = New-Object System.Collections.ArrayList
function Add-Result($name, $status, $detail) { [void]$Results.Add([pscustomobject]@{ Check = $name; Status = $status; Detail = $detail }) }
function Write-Step($t) { Write-Host ""; Write-Host "==> $t" -ForegroundColor Cyan }
function Write-Ok($t)   { Write-Host "    [OK]   $t" -ForegroundColor Green }
function Write-Warn2($t){ Write-Host "    [WARN] $t" -ForegroundColor Yellow }
function Write-Bad($t)  { Write-Host "    [FAIL] $t" -ForegroundColor Red }
function Write-Info($t) { Write-Host "           $t" -ForegroundColor Gray }
function Ask-YesNo($q, $default = 'Y') {
    $suffix = if ($default -eq 'Y') { '[Y/n]' } else { '[y/N]' }
    $a = Read-Host "    $q $suffix"
    if ([string]::IsNullOrWhiteSpace($a)) { $a = $default }
    return ($a.Trim().ToUpper().StartsWith('Y'))
}
function Invoke-Native([scriptblock]$sb) { $ErrorActionPreference = 'Continue'; & $sb 2>&1 | ForEach-Object { "$_" } }
function Stop-Install($msg) {
    Write-Bad $msg
    Write-Host ""
    Write-Host "Installation stopped. Log file: $LogFile" -ForegroundColor Red
    Write-Host "The folder with the log will now open (file is selected). Send it to your BIM/IT support." -ForegroundColor Red
    try { Stop-Transcript | Out-Null } catch {}
    try { Start-Process explorer.exe -ArgumentList "/select,`"$LogFile`"" } catch {}
    exit 1
}
function Download($url, $out) {
    Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -TimeoutSec 900 -Headers @{ 'User-Agent' = 'Petrocon-NavisMCP-installer' } -ErrorAction Stop
    Unblock-File $out -ErrorAction SilentlyContinue
}
# Latest release tag WITHOUT the GitHub API (no 60-requests/hour office limit)
function Get-LatestTag($repo) {
    $r = Invoke-WebRequest -Uri "https://github.com/$repo/releases/latest" -UseBasicParsing -TimeoutSec 60 -ErrorAction Stop
    $tag = ($r.BaseResponse.ResponseUri.AbsoluteUri -split '/')[-1]
    if ($tag -notmatch '\d+\.\d+') { throw "Could not read the latest release of $repo" }
    return $tag
}
function Get-AssetNames($repo, $tag) {
    $html = (Invoke-WebRequest -Uri "https://github.com/$repo/releases/expanded_assets/$tag" -UseBasicParsing -TimeoutSec 60 -ErrorAction Stop).Content
    $names = @()
    foreach ($m in [regex]::Matches($html, 'href="/[^"]+/releases/download/[^"/]+/([^"]+)"')) { $names += [uri]::UnescapeDataString($m.Groups[1].Value) }
    return @($names | Select-Object -Unique)
}
function Get-ExpectedHash($sumsText, $fileName) {
    foreach ($line in ($sumsText -split "`n")) {
        $m = [regex]::Match($line.Trim(), '^([a-fA-F0-9]{64})\s+\*?(.+)$')
        if ($m.Success -and $m.Groups[2].Value.Trim() -eq $fileName) { return $m.Groups[1].Value.ToUpperInvariant() }
    }
    return $null
}
function Test-McpServer($exe, $exeArgs, $label) {
    # Talk to the server like Claude does: keep the pipe open, send
    # initialize, wait for the answer, then ask for the tool list.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    if ($exeArgs) { $psi.Arguments = $exeArgs }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"installer-check","version":"1.0"}}}')
    $p.StandardInput.Flush()
    $lines = @(); $sentList = $false; $gotList = $false
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $gotList) {
        $t = $p.StandardOutput.ReadLineAsync()
        while (-not $t.Wait(1000)) { if ((Get-Date) -ge $deadline -or $p.HasExited) { break } }
        if (-not $t.IsCompleted) { break }
        $line = $t.Result
        if ($null -eq $line) { break }
        $lines += $line
        if (-not $sentList -and $line -match '"id"\s*:\s*1\b') {
            $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
            $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
            $p.StandardInput.Flush()
            $sentList = $true
        }
        if ($line -match '"id"\s*:\s*2\b') { $gotList = $true }
    }
    try { $p.StandardInput.Close() } catch {}
    if (-not $p.WaitForExit(5000)) { try { $p.Kill() } catch {} }
    $e = ''; try { if ($errTask.Wait(3000)) { $e = $errTask.Result } } catch {}
    $o = $lines -join "`n"
    [IO.File]::WriteAllText((Join-Path $WorkDir "test-$label-out.txt"), $o)
    [IO.File]::WriteAllText((Join-Path $WorkDir "test-$label-err.txt"), $e)
    $count = ([regex]::Matches($o, '"name"\s*:\s*"[A-Za-z0-9]+_[A-Za-z0-9_]+"')).Count
    return [pscustomobject]@{ Ok = ($gotList -and $o -match '"tools"'); Tools = $count; Err = $e; Out = $o }
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor White
Write-Host "   Navisworks MCP - installer and health check  (version $InstallerVersion)" -ForegroundColor White
Write-Host "  ============================================================" -ForegroundColor White
Write-Host "   Log file: $LogFile" -ForegroundColor Gray
# --- A word from the people who saved you an afternoon of clicking ---
Write-Host ""
Write-Host "  ************************************************************" -ForegroundColor Magenta
Write-Host "   BEFORE WE BEGIN... A MOMENT OF GRATITUDE, PLEASE" -ForegroundColor Yellow
Write-Host "  ************************************************************" -ForegroundColor Magenta
Write-Host ""
Write-Host "   This installer was going to take you 2 hours, 14 error"
Write-Host "   messages and at least one cup of chai you forgot to drink."
Write-Host "   Now it takes one double-click. You're welcome."
Write-Host ""
Write-Host "   Kindly direct your applause, compliments and samosas to:" -ForegroundColor Cyan
Write-Host ""
Write-Host "     * RAHID      - who fought Smart App Control, PyYAML," -ForegroundColor Green
Write-Host "                    Python 3.15 and GitHub, and won." -ForegroundColor Green
Write-Host "     * IMTHIAZ AHMED - who believed robots should do the" -ForegroundColor Green
Write-Host "                    boring stuff so engineers don't have to." -ForegroundColor Green
Write-Host "     * PETROCON MANAGEMENT - for funding our dreams of" -ForegroundColor Green
Write-Host "                    never installing anything manually again." -ForegroundColor Green
Write-Host ""
Write-Host "   Please say 'Thank you!' out loud now." -ForegroundColor Yellow
Write-Host "   (Yes, out loud. Your colleagues will understand.)" -ForegroundColor Yellow
Write-Host ""
Write-Host "   Fun fact: an AI wrote most of this script, but it only" -ForegroundColor Gray
Write-Host "   accepts thanks in the form of well-modelled Revit families." -ForegroundColor Gray
Write-Host "  ************************************************************" -ForegroundColor Magenta
Write-Host ""
Write-Host "   OFFICIAL ANNOUNCEMENT FROM THE BIM DEPARTMENT:" -ForegroundColor Cyan
Write-Host "   Civil, Structure, Mechanical, Piping, Electrical, Instrumentation:" -ForegroundColor White
Write-Host "   brilliant teams, every one of them. BIM's job? Keep them all" -ForegroundColor White
Write-Host "   standing tall, together, in one model. Proof:" -ForegroundColor White
Write-Host "     - Every great design deserves a great model. BIM builds it." -ForegroundColor White
Write-Host "     - Every clash caught on screen, not on site? BIM had your back." -ForegroundColor White
Write-Host "     - Every discipline brings a piece. BIM makes it one picture." -ForegroundColor White
Write-Host "     - BIM never stands above anyone. It just stands UPRIGHT," -ForegroundColor White
Write-Host "       always. (Level 0, plumb, no warnings.)" -ForegroundColor White
Write-Host ""
$null = Read-Host "   Press Enter to start (BIM stays upright, the installer gets to work)"

# ---------------------------------------------------------------------
# 1. PRE-CHECKS
# ---------------------------------------------------------------------
Write-Step "1/6  Checking this computer"
if (-not [Environment]::Is64BitOperatingSystem) { Stop-Install "64-bit Windows is required." }
Write-Ok "$((Get-CimInstance Win32_OperatingSystem).Caption) (64-bit)"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Write-Warn2 "This window is running as Administrator. Everything installs per user."
    if (-not (Ask-YesNo "Continue anyway?" 'N')) { Stop-Install "Close this window and double-click the file normally (not as administrator)." }
} else { Write-Ok "Running as normal user: $env:USERNAME" }

$SacOn = $false
$sacv = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name 'VerifiedAndReputablePolicyState' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
if ($sacv -eq 1 -or $sacv -eq 2) { $SacOn = $true }
if ($SacOn) {
    Write-Warn2 "Smart App Control is ON."
    Write-Info  "NavisCoord is digitally signed and should work. nwd-mcp is not signed,"
    Write-Info  "so Windows may block it. IT can turn Smart App Control off if needed."
    Add-Result 'Smart App Control' 'WARN' 'On - nwd-mcp may be blocked'
} else { Write-Ok "Smart App Control: off"; Add-Result 'Smart App Control' 'OK' 'Off' }

$netOk = $true
foreach ($h in @(@{N='GitHub (releases)';U='https://github.com'}, @{N='GitHub downloads';U='https://release-assets.githubusercontent.com'})) {
    try { Invoke-WebRequest -Uri $h.U -UseBasicParsing -Method Head -TimeoutSec 20 -ErrorAction Stop | Out-Null; Write-Ok "Reachable: $($h.N)" }
    catch {
        $code = $null; try { $code = [int]$_.Exception.Response.StatusCode } catch {}
        if ($code -and $code -lt 500) { Write-Ok "Reachable: $($h.N) (HTTP $code)" } else { Write-Bad "Cannot reach $($h.N): $($h.U)"; $netOk = $false }
    }
}
if (-not $netOk) {
    Write-Info "Ask IT to allow: github.com, objects.githubusercontent.com, release-assets.githubusercontent.com"
    if (-not (Ask-YesNo "Try to continue anyway?" 'N')) { Stop-Install "No access to GitHub." }
}

# Navisworks Manage versions
$NavYears = @()
foreach ($y in 2022..2027) {
    if (Test-Path (Join-Path $env:ProgramFiles "Autodesk\Navisworks Manage $y\Autodesk.Navisworks.Api.dll")) { $NavYears += $y }
}
if ($NavYears.Count -eq 0) {
    Add-Result 'Navisworks Manage' 'FAIL' 'Not found'
    Stop-Install "Navisworks Manage was not found (Simulate/Freedom are not supported by these MCPs)."
}
Write-Ok "Navisworks Manage found: $($NavYears -join ', ')"
Add-Result 'Navisworks Manage' 'OK' ($NavYears -join ', ')

# Claude Desktop + its 3 possible settings locations
$claudeExe   = Join-Path $env:LOCALAPPDATA 'AnthropicClaude\claude.exe'
$claudeStore = @(); if (Test-Path "$env:LOCALAPPDATA\Packages") { $claudeStore = @(Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue) }
$claudeData  = @(); if (Test-Path "$env:LOCALAPPDATA\Claude-Data") { $claudeData = @(Get-ChildItem "$env:LOCALAPPDATA\Claude-Data" -Directory -ErrorAction SilentlyContinue) }
foreach ($pkg in $claudeStore) {
    $cd = Join-Path $env:LOCALAPPDATA ("Claude-Data\" + $pkg.Name.ToLower())
    if (-not ($claudeData | Where-Object { $_.FullName -eq $cd })) { New-Item -ItemType Directory -Force -Path $cd | Out-Null; $claudeData += Get-Item $cd }
}
$claudeApp = $null; try { $claudeApp = Get-StartApps | Where-Object { $_.Name -eq 'Claude' } | Select-Object -First 1 } catch {}
$claudeInstalled = (Test-Path $claudeExe) -or ($claudeStore.Count -gt 0) -or ($null -ne $claudeApp)
if ($claudeInstalled) { Write-Ok "Claude Desktop is installed"; Add-Result 'Claude Desktop' 'OK' '' }
else {
    Write-Warn2 "Claude Desktop was not found. Install it from https://claude.ai/download and sign in."
    Add-Result 'Claude Desktop' 'WARN' 'Not found'
    if (Ask-YesNo "Open the download page now?" 'Y') { Start-Process 'https://claude.ai/download' }
    if (-not (Ask-YesNo "Continue (Claude settings will be prepared for later)?" 'Y')) { Stop-Install "Install Claude Desktop first." }
}

Write-Host ""
$DoNwd = Ask-YesNo "Install nwd-mcp (model browsing, properties, search sets, viewpoints)?" 'Y'
$DoNc  = Ask-YesNo "Install NavisCoord (clash coordination, ranking, reports)?" 'Y'
if (-not $DoNwd -and -not $DoNc) { Stop-Install "Nothing selected." }

# Navisworks must be closed to replace plug-ins
while (Get-Process -Name 'Roamer' -ErrorAction SilentlyContinue) {
    Write-Warn2 "Navisworks is running. Save your work and close Navisworks."
    $null = Read-Host "    Press Enter when Navisworks is closed"
}

$NwdExe = $null; $NcExe = $null

# ---------------------------------------------------------------------
# 2. nwd-mcp
# ---------------------------------------------------------------------
if ($DoNwd) {
    Write-Step "2/6  nwd-mcp (Bimwright)"
    try {
        $tag = Get-LatestTag 'bimwright/nwd-mcp'
        Write-Ok "Latest release: $tag"
        $zipName = "NwdMcp.Setup-$tag-win-x64.zip"
        $zip = Join-Path $WorkDir $zipName
        Write-Info "Downloading $zipName ..."
        Download "https://github.com/bimwright/nwd-mcp/releases/download/$tag/$zipName" $zip
        # checksum (published since v1.1.0)
        $shaFile = Join-Path $WorkDir "$zipName.sha256"
        $verified = $false
        try {
            Download "https://github.com/bimwright/nwd-mcp/releases/download/$tag/$zipName.sha256" $shaFile
            $exp = Get-ExpectedHash (Get-Content $shaFile -Raw) $zipName
            if ($exp) {
                if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $exp) { throw "SHA-256 mismatch for $zipName" }
                $verified = $true
            }
        } catch { if ($_.Exception.Message -match 'mismatch') { throw } }
        if ($verified) { Write-Ok "Checksum verified" } else { Write-Warn2 "No checksum published for $tag; continuing" }
        $setup = Join-Path $WorkDir 'nwd-setup'
        Expand-Archive $zip -DestinationPath $setup -Force
        Get-ChildItem $setup -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
        $inst = Get-ChildItem $setup -Recurse -Filter 'install.ps1' | Select-Object -First 1
        if (-not $inst) { throw "install.ps1 not found in $zipName" }
        # Plug-in years included in this release
        $mf = Get-ChildItem $setup -Recurse -Filter 'manifest.json' | Select-Object -First 1
        if ($mf) {
            $mfText = Get-Content $mf.FullName -Raw
            $packed = @([regex]::Matches($mfText, '20(2[2-9])') | ForEach-Object { [int]$_.Value } | Select-Object -Unique)
            $missing = @($NavYears | Where-Object { $packed -notcontains $_ })
            if ($packed.Count -gt 0) { Write-Info "Plug-in years in this release: $($packed -join ', ')" }
            if ($missing.Count -gt 0) { Write-Warn2 "nwd-mcp $tag has no plug-in for Navisworks $($missing -join ', ')"; Add-Result 'nwd-mcp years' 'WARN' "No plug-in for $($missing -join ', ')" }
        }
        Write-Info "Running the official nwd-mcp installer ..."
        $io = Invoke-Native { powershell -NoProfile -ExecutionPolicy Bypass -File $inst.FullName }
        $io | Select-Object -Last 6 | ForEach-Object { Write-Info $_ }
        $NwdExe = Join-Path $env:LOCALAPPDATA 'Bimwright\nwd-mcp\server\current\nwd-mcp.exe'
        if (-not (Test-Path $NwdExe)) { throw "nwd-mcp.exe was not installed ($NwdExe)" }
        if (-not (Test-Path (Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\Bimwright.Nwd.bundle'))) { throw "Bimwright.Nwd.bundle was not installed" }
        Write-Ok "Plug-in: %APPDATA%\Autodesk\ApplicationPlugins\Bimwright.Nwd.bundle"
        Write-Ok "Server : $NwdExe"
        Add-Result 'nwd-mcp install' 'OK' $tag
    } catch {
        Write-Bad "nwd-mcp: $($_.Exception.Message)"
        Add-Result 'nwd-mcp install' 'FAIL' $_.Exception.Message
        $NwdExe = $null
    }
}

# ---------------------------------------------------------------------
# 3. NavisCoord
# ---------------------------------------------------------------------
if ($DoNc) {
    Write-Step "3/6  NavisCoord (HorizunGroup)"
    try {
        $repo = 'HorizunGroup/naviscoord-mcp'
        $tag = Get-LatestTag $repo
        $ver = $tag.TrimStart('v')
        Write-Ok "Latest release: $tag"
        $base = "https://github.com/$repo/releases/download/$tag"
        $sumsPath = Join-Path $WorkDir 'NavisCoord-SHA256SUMS.txt'
        Download "$base/SHA256SUMS.txt" $sumsPath
        $sums = Get-Content $sumsPath -Raw

        # Add-in per Navisworks year (NavisCoord supports 2024-2026)
        $ncYears = @($NavYears | Where-Object { $_ -ge 2024 -and $_ -le 2026 })
        if ($ncYears.Count -eq 0) { Write-Warn2 "NavisCoord supports Navisworks Manage 2024-2026 only; none installed." }
        foreach ($y in $ncYears) {
            $an = "NavisCoord-$ver-addin-NW$y.zip"
            $az = Join-Path $WorkDir $an
            Write-Info "[$y] downloading $an ..."
            Download "$base/$an" $az
            $exp = Get-ExpectedHash $sums $an
            if (-not $exp) { throw "$an is not listed in SHA256SUMS.txt" }
            if ((Get-FileHash $az -Algorithm SHA256).Hash -ne $exp) { throw "SHA-256 mismatch for $an" }
            $stage = Join-Path $WorkDir "nc-$y"
            Expand-Archive $az -DestinationPath $stage -Force
            $dest = Join-Path $env:APPDATA "Autodesk\Navisworks Manage $y\Plugins\NavisCoord"
            if (Test-Path $dest) {
                $bk = Join-Path $env:TEMP "NavisCoord-NW$y-backup-$Stamp"
                Copy-Item $dest $bk -Recurse -Force
                Write-Info "[$y] previous add-in backed up to $bk"
            }
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
            Copy-Item (Join-Path $stage '*') $dest -Recurse -Force    # keeps the user's own profiles
            Get-ChildItem $dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
            Write-Ok "[$y] add-in installed and checksum verified: $dest"
        }

        # Standalone runtime (Python included - nothing else to install)
        $rn = "naviscoord-runtime-$ver-win-x64.zip"
        $rz = Join-Path $WorkDir $rn
        Write-Info "Downloading $rn (bundled Python runtime) ..."
        Download "$base/$rn" $rz
        $exp = Get-ExpectedHash $sums $rn
        if (-not $exp) { throw "$rn is not listed in SHA256SUMS.txt" }
        if ((Get-FileHash $rz -Algorithm SHA256).Hash -ne $exp) { throw "SHA-256 mismatch for $rn" }
        $rtRoot = Join-Path $env:LOCALAPPDATA "NavisCoord\portable\$ver"
        if (Test-Path $rtRoot) { Move-Item $rtRoot "$rtRoot.backup-$Stamp" -Force }
        Expand-Archive $rz -DestinationPath $rtRoot -Force
        Get-ChildItem $rtRoot -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
        $NcExe = Get-ChildItem $rtRoot -Recurse -Filter 'naviscoord-mcp.exe' | Select-Object -First 1 -ExpandProperty FullName
        if (-not $NcExe) { throw "naviscoord-mcp.exe not found in $rn" }
        $sig = (Get-AuthenticodeSignature $NcExe).Status
        Write-Ok "Server : $NcExe (signature: $sig)"
        Add-Result 'NavisCoord install' 'OK' "$tag, add-in for $($ncYears -join ', ')"
    } catch {
        Write-Bad "NavisCoord: $($_.Exception.Message)"
        Add-Result 'NavisCoord install' 'FAIL' $_.Exception.Message
        $NcExe = $null
    }
}

# ---------------------------------------------------------------------
# 4. SERVER TESTS
# ---------------------------------------------------------------------
Write-Step "4/6  Testing the servers (MCP handshake, no Navisworks needed)"
if ($NwdExe) {
    $t = Test-McpServer $NwdExe $null 'nwd'
    if ($t.Ok) { Write-Ok "nwd-mcp answered with $($t.Tools) tools"; Add-Result 'nwd-mcp test' 'OK' "$($t.Tools) tools" }
    else {
        Write-Bad "nwd-mcp did not answer."; ($t.Err -split "`n" | Select-Object -Last 8) | ForEach-Object { Write-Info $_ }
        if ($t.Err -match 'Application Control|blocked') { Write-Info "Windows Smart App Control blocked nwd-mcp. Ask IT to turn it off." }
        Add-Result 'nwd-mcp test' 'FAIL' 'See log'; $NwdExe = $null
    }
}
if ($NcExe) {
    $t = Test-McpServer $NcExe $null 'nc'
    if ($t.Ok) { Write-Ok "NavisCoord answered with $($t.Tools) tools"; Add-Result 'NavisCoord test' 'OK' "$($t.Tools) tools" }
    else {
        Write-Bad "NavisCoord did not answer."; ($t.Err -split "`n" | Select-Object -Last 8) | ForEach-Object { Write-Info $_ }
        Add-Result 'NavisCoord test' 'FAIL' 'See log'; $NcExe = $null
    }
}

# ---------------------------------------------------------------------
# 5. CLAUDE DESKTOP
# ---------------------------------------------------------------------
Write-Step "5/6  Connecting Claude Desktop"
function Set-ClaudeServer($path, $name, $entry) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $cfg = $null
    if (Test-Path $path) {
        if (-not (Test-Path "$path.backup-$Stamp")) { Copy-Item $path "$path.backup-$Stamp" -Force }
        $raw = Get-Content $path -Raw
        if ($raw -and $raw.Trim()) { try { $cfg = $raw | ConvertFrom-Json } catch { Write-Warn2 "Existing config is not valid JSON; backup kept: $path.backup-$Stamp" } }
    }
    if (-not $cfg) { $cfg = New-Object psobject }
    if (-not ($cfg.PSObject.Properties.Name -contains 'mcpServers') -or $null -eq $cfg.mcpServers) { $cfg | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue (New-Object psobject) -Force }
    $cfg.mcpServers | Add-Member -NotePropertyName $name -NotePropertyValue $entry -Force
    [IO.File]::WriteAllText($path, ($cfg | ConvertTo-Json -Depth 32), (New-Object System.Text.UTF8Encoding($false)))
    $null = (Get-Content $path -Raw | ConvertFrom-Json).mcpServers
}
$configs = @(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
foreach ($pkg in $claudeStore) { $configs += Join-Path $pkg.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json' }
foreach ($cd in $claudeData)   { $configs += Join-Path $cd.FullName 'claude_desktop_config.json' }
$entries = @()
if ($NwdExe) { $entries += ,@('nwd-mcp',    [pscustomobject][ordered]@{ command = $NwdExe; args = @() }) }
if ($NcExe)  { $entries += ,@('NavisCoord', [pscustomobject][ordered]@{ command = $NcExe;  args = @(); env = [pscustomobject]@{ NAVISCOORD_READ_ONLY = '0' } }) }
if ($entries.Count -eq 0) { Add-Result 'Claude config' 'FAIL' 'No server installed'; Stop-Install "No Navisworks MCP server could be installed." }
foreach ($c in $configs) {
    try { foreach ($e in $entries) { Set-ClaudeServer $c $e[0] $e[1] }; Write-Ok "Added $(($entries | ForEach-Object { $_[0] }) -join ' + ') to $c" }
    catch { Write-Bad "Could not update $c : $($_.Exception.Message)" }
}
Add-Result 'Claude config' 'OK' (($entries | ForEach-Object { $_[0] }) -join ', ')

# Developer Mode
$devFiles = @(Join-Path $env:APPDATA 'Claude\developer_settings.json')
foreach ($pkg in $claudeStore) { $devFiles += Join-Path $pkg.FullName 'LocalCache\Roaming\Claude\developer_settings.json' }
foreach ($cd in $claudeData)   { $devFiles += Join-Path $cd.FullName 'developer_settings.json' }
foreach ($df in $devFiles) {
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path $df) | Out-Null
        $d = $null
        if (Test-Path $df) { Copy-Item $df "$df.backup-$Stamp" -Force; try { $d = Get-Content $df -Raw | ConvertFrom-Json } catch {} }
        if (-not $d) { $d = New-Object psobject }
        $d | Add-Member -NotePropertyName 'allowDevTools' -NotePropertyValue $true -Force
        [IO.File]::WriteAllText($df, ($d | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
    } catch {}
}
Write-Ok "Claude Developer Mode enabled"

# Duplicate check: .mcpb extensions of the same servers
$extHits = @()
foreach ($root in @((Join-Path $env:APPDATA 'Claude')) + @($claudeStore | ForEach-Object { Join-Path $_.FullName 'LocalCache\Roaming\Claude' }) + @($claudeData | ForEach-Object { $_.FullName })) {
    if (Test-Path $root) { $extHits += @(Get-ChildItem $root -Directory -Recurse -Depth 2 -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'nwd.?mcp|naviscoord' }) }
}
if ($extHits.Count -gt 0) {
    Write-Warn2 "A Navisworks MCP extension (.mcpb) also seems to be installed:"
    $extHits | ForEach-Object { Write-Info $_.FullName }
    Write-Info "Remove it in Claude (Settings > Extensions) so Claude does not run two copies."
    Add-Result 'No duplicate extension' 'WARN' 'Remove the .mcpb extension'
}

# ---------------------------------------------------------------------
# 6. RESTART CLAUDE + SUMMARY
# ---------------------------------------------------------------------
Write-Step "6/6  Finishing"
if ($claudeInstalled) {
    $running = Get-Process -Name 'claude' -ErrorAction SilentlyContinue
    if ($running -and (Ask-YesNo "Claude must restart to load the Navisworks MCPs. Close and reopen Claude now?" 'Y')) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 3
    }
    if (-not (Get-Process -Name 'claude' -ErrorAction SilentlyContinue)) {
        try { if ($claudeApp) { Start-Process "shell:AppsFolder\$($claudeApp.AppID)" } elseif (Test-Path $claudeExe) { Start-Process $claudeExe }; Write-Ok "Claude Desktop started" } catch {}
    }
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor White
Write-Host "   Summary" -ForegroundColor White
Write-Host "  ============================================================" -ForegroundColor White
foreach ($r in $Results) {
    $col = switch ($r.Status) { 'OK' { 'Green' } 'WARN' { 'Yellow' } default { 'Red' } }
    Write-Host ("   {0,-6} {1,-24} {2}" -f $r.Status, $r.Check, $r.Detail) -ForegroundColor $col
}
Write-Host ""
Write-Host "   Next steps:" -ForegroundColor White
Write-Host "   1. Open Navisworks Manage and a model (.nwd/.nwf). New ribbon tabs appear"
Write-Host "      for the installed plug-ins."
Write-Host "   2. In Claude Desktop: Settings > Developer should show nwd-mcp and/or"
Write-Host "      NavisCoord as running."
Write-Host "   3. Start a NEW chat and ask, for example:"
Write-Host "      'Use nwd-mcp to show the model tree and statistics of my open model.'"
Write-Host "      'Use NavisCoord to check health and list the clash tests.'"
Write-Host ""
Write-Host "   If something fails: send this log file to support: $LogFile" -ForegroundColor Gray
try { Stop-Transcript | Out-Null } catch {}
if ($Results | Where-Object { $_.Status -eq 'FAIL' }) { exit 1 } else { exit 0 }
