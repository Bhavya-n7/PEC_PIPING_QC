<# :
@echo off
REM ===================================================================
REM  Civil 3D MCP - one-click installer and health check (version 3)
REM  Double-click this file. Do NOT use "Run as administrator".
REM  The PowerShell code below this header does all the work.
REM ===================================================================
setlocal
set "C3DMCP_SCRIPT=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText($env:C3DMCP_SCRIPT); Invoke-Expression $s"
set "C3DMCP_RC=%errorlevel%"
echo.
echo Finished. Scroll up to read the results, then press any key to close this window.
pause >nul
exit /b %C3DMCP_RC%
#>

# =====================================================================
#  Civil 3D MCP - installer (PowerShell part)
#  Source project: https://github.com/barbosaihan/civil3d-mcp (MIT)
#  What it does:
#   1. Pre-checks: Windows, user, internet, Civil 3D 2025/2026, Claude
#   2. Downloads the Civil 3D MCP source (pinned, tested version)
#   3. Node.js (portable, no admin) -> builds the MCP server
#   4. .NET 8 SDK (user folder, no admin) -> builds the Civil 3D plugin
#      against THIS PC's Civil 3D DLLs
#   5. Installs the plugin as an auto-loading bundle (no NETLOAD needed)
#   6. Test: MCP handshake with the server
#   7. Adds "civil3d" to Claude Desktop (all 3 settings locations),
#      enables Developer Mode, restarts Claude
#  Safe to run again: it backs up what it changes.
# =====================================================================

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

$InstallerVersion = '3'
$RepoOwner  = 'barbosaihan'
$RepoName   = 'civil3d-mcp'
$RepoCommit = '344ad15ff517a19232fdf4eda2685292bdf723cf'   # tested 18-Nov-2026
$Port       = 8080                                          # fixed in the plugin
$Root       = Join-Path $env:LOCALAPPDATA 'Civil3DMCP'
$SrcDir     = Join-Path $Root 'civil3d-mcp'
$NodeDir    = Join-Path $Root 'node'
$DotnetDir  = Join-Path $Root 'dotnet'
$BundleDir  = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\Civil3dMcp.bundle'
$Stamp      = Get-Date -Format 'yyyyMMdd-HHmmss'
$WorkDir    = Join-Path $env:TEMP "Civil3DMCP-install-$Stamp"
$LogFile    = Join-Path $env:TEMP "Civil3DMCP-install-$Stamp.log"
New-Item -ItemType Directory -Force -Path $WorkDir, $Root | Out-Null
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
function Invoke-Native([scriptblock]$sb) {
    # Run an external program without PowerShell 5.1 turning its warnings into fatal errors
    $ErrorActionPreference = 'Continue'
    & $sb 2>&1 | ForEach-Object { "$_" }
}
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
    Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -TimeoutSec 600 -ErrorAction Stop
    Unblock-File $out -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor White
Write-Host "   Civil 3D MCP - installer and health check  (version $InstallerVersion)" -ForegroundColor White
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
Write-Step "1/7  Checking this computer"
if (-not [Environment]::Is64BitOperatingSystem) { Stop-Install "64-bit Windows is required." }
Write-Ok "$((Get-CimInstance Win32_OperatingSystem).Caption) (64-bit)"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Write-Warn2 "This window is running as Administrator. Everything installs per user."
    if (-not (Ask-YesNo "Continue anyway?" 'N')) { Stop-Install "Close this window and double-click the file normally (not as administrator)." }
} else { Write-Ok "Running as normal user: $env:USERNAME" }

# Smart App Control blocks unsigned DLLs inside Civil 3D (same as the Revit add-in)
$SacOn = $false
try {
    $v = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name 'VerifiedAndReputablePolicyState' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
    if ($v -eq 1 -or $v -eq 2) { $SacOn = $true }
} catch {}
if ($SacOn) {
    Write-Warn2 "Smart App Control is ON. Civil 3D will block the MCP plugin DLL (it is not signed)."
    Write-Info  "Ask IT to turn it off: Windows Security > App & browser control > Smart App Control > Off"
    if (-not (Ask-YesNo "Continue anyway?" 'N')) { Add-Result 'Smart App Control' 'FAIL' 'On'; Stop-Install "Smart App Control is on." }
    Add-Result 'Smart App Control' 'WARN' 'On - plugin will be blocked'
} else { Write-Ok "Smart App Control: off"; Add-Result 'Smart App Control' 'OK' 'Off' }

# Internet
$netOk = $true
foreach ($h in @(@{N='GitHub (source)';U='https://github.com'}, @{N='nodejs.org (Node.js)';U='https://nodejs.org/dist/index.json'}, @{N='npm registry';U='https://registry.npmjs.org/zod'}, @{N='Microsoft .NET download';U='https://dot.net/v1/dotnet-install.ps1'}, @{N='NuGet (Roslyn package)';U='https://api.nuget.org/v3/index.json'})) {
    try { Invoke-WebRequest -Uri $h.U -UseBasicParsing -Method Head -TimeoutSec 20 -ErrorAction Stop | Out-Null; Write-Ok "Reachable: $($h.N)" }
    catch {
        $code = $null; try { $code = [int]$_.Exception.Response.StatusCode } catch {}
        if ($code -and $code -lt 500) { Write-Ok "Reachable: $($h.N) (HTTP $code)" } else { Write-Bad "Cannot reach $($h.N): $($h.U)"; $netOk = $false }
    }
}
if ($netOk) { Add-Result 'Internet access' 'OK' '' } else {
    Add-Result 'Internet access' 'FAIL' 'Some sites blocked'
    Write-Info "Ask IT to allow: github.com, codeload.github.com, nodejs.org, registry.npmjs.org, dot.net, builds.dotnet.microsoft.com, api.nuget.org"
    if (-not (Ask-YesNo "Try to continue anyway?" 'N')) { Stop-Install "No internet access to required sites." }
}

# Civil 3D 2025/2026 (the plugin targets .NET 8 = Civil 3D 2025 and later)
$c3d = @()
Get-ChildItem "$env:ProgramFiles\Autodesk" -Directory -Filter 'AutoCAD 20*' -ErrorAction SilentlyContinue | ForEach-Object {
    $y = [int]([regex]::Match($_.Name, '20\d\d').Value)
    $aecc = Get-ChildItem $_.FullName -Recurse -Depth 2 -Filter 'AeccDbMgd.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($aecc) { $c3d += [pscustomobject]@{ Year = $y; Dir = $_.FullName } }
}
$c3dOld = @($c3d | Where-Object { $_.Year -lt 2025 })
$c3d    = @($c3d | Where-Object { $_.Year -ge 2025 } | Sort-Object Year)
if ($c3dOld.Count -gt 0) { Write-Info "Civil 3D $($($c3dOld | ForEach-Object { $_.Year }) -join ', ') found - not supported by this MCP (needs 2025 or later)." }
if ($c3d.Count -eq 0) { Add-Result 'Civil 3D 2025+' 'FAIL' 'Not found'; Stop-Install "Civil 3D 2025 or later was not found in $env:ProgramFiles\Autodesk." }
$Years = @($c3d | ForEach-Object { $_.Year })
Write-Ok "Civil 3D found: $($Years -join ', ')"
if ($Years | Where-Object { $_ -ge 2027 }) { Write-Warn2 "Civil 3D 2027+ is newer than this MCP was tested with; the plugin may not load there." }
Add-Result 'Civil 3D 2025+' 'OK' ($Years -join ', ')
$BuildC3D = $c3d[0]   # build against the oldest supported version (forward compatible)

# Port 8080
try {
    $l = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($l) {
        $pn = (Get-Process -Id $l.OwningProcess -ErrorAction SilentlyContinue).ProcessName
        if ($pn -match 'acad') { Write-Ok "Port $Port is used by Civil 3D (plugin already running)" }
        else { Write-Warn2 "Port $Port is already used by '$pn'. The plugin needs this port; close that program before using Civil 3D MCP."; Add-Result "Port $Port free" 'WARN' "Used by $pn" }
    } else { Write-Ok "Port $Port is free" }
} catch { Write-Ok "Port $Port is free" }

# Claude Desktop (+ where it keeps its settings)
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

# ---------------------------------------------------------------------
# 2. SOURCE
# ---------------------------------------------------------------------
Write-Step "2/7  Downloading Civil 3D MCP (tested version $($RepoCommit.Substring(0,7)))"
$zip = Join-Path $WorkDir 'civil3d-mcp.zip'
try { Download "https://github.com/$RepoOwner/$RepoName/archive/$RepoCommit.zip" $zip }
catch { Add-Result 'Source download' 'FAIL' $_.Exception.Message; Stop-Install "Could not download the Civil 3D MCP source from GitHub." }
$ex = Join-Path $WorkDir 'src'
Expand-Archive $zip -DestinationPath $ex -Force
$inner = Get-ChildItem $ex -Directory | Select-Object -First 1
if (Test-Path $SrcDir) { Move-Item $SrcDir "$SrcDir.backup-$Stamp" -Force; Write-Info "Previous copy kept as $SrcDir.backup-$Stamp" }
Move-Item $inner.FullName $SrcDir
Write-Ok "Source in $SrcDir"
Add-Result 'Source' 'OK' $RepoCommit.Substring(0,7)

# ---------------------------------------------------------------------
# 3. NODE.JS + MCP SERVER
# ---------------------------------------------------------------------
Write-Step "3/7  Node.js and the MCP server"
$nodeExe = Join-Path $NodeDir 'node.exe'
if (-not (Test-Path $nodeExe)) {
    try {
        $idx = Invoke-RestMethod 'https://nodejs.org/dist/index.json' -UseBasicParsing -TimeoutSec 60 -ErrorAction Stop
        $rel = $idx | Where-Object { $_.version -like 'v22.*' -and $_.files -contains 'win-x64-zip' } | Select-Object -First 1
        $nv  = $rel.version
        Write-Info "Downloading portable Node.js $nv (no admin needed) ..."
        $nz = Join-Path $WorkDir "node-$nv-win-x64.zip"
        Download "https://nodejs.org/dist/$nv/node-$nv-win-x64.zip" $nz
        Expand-Archive $nz -DestinationPath $WorkDir -Force
        if (Test-Path $NodeDir) { Remove-Item $NodeDir -Recurse -Force }
        Move-Item (Join-Path $WorkDir "node-$nv-win-x64") $NodeDir
    } catch { Add-Result 'Node.js' 'FAIL' $_.Exception.Message; Stop-Install "Could not install Node.js." }
}
$env:Path = "$NodeDir;$env:Path"
$env:NPM_CONFIG_UPDATE_NOTIFIER = 'false'; $env:NPM_CONFIG_FUND = 'false'
$nodeVer = (Invoke-Native { & $nodeExe --version }) -join ''
Write-Ok "Node.js $nodeVer ($nodeExe)"
Add-Result 'Node.js' 'OK' $nodeVer

Write-Info "Installing server packages and building (about 1 minute) ..."
Push-Location $SrcDir
$o1 = Invoke-Native { & (Join-Path $NodeDir 'npm.cmd') ci --no-audit --no-fund }; $e1 = $LASTEXITCODE
$o2 = Invoke-Native { & (Join-Path $NodeDir 'npm.cmd') run build }; $e2 = $LASTEXITCODE
Pop-Location
$ServerJs = Join-Path $SrcDir 'build\index.js'
if ($e1 -ne 0 -or $e2 -ne 0 -or -not (Test-Path $ServerJs)) {
    ($o1 + $o2) | Select-Object -Last 15 | ForEach-Object { Write-Info $_ }
    Add-Result 'MCP server build' 'FAIL' 'npm ci / build failed'
    Stop-Install "Could not build the MCP server."
}
Write-Ok "MCP server built: $ServerJs"
Add-Result 'MCP server build' 'OK' ''

# ---------------------------------------------------------------------
# 4. .NET 8 SDK + CIVIL 3D PLUGIN
# ---------------------------------------------------------------------
Write-Step "4/7  Building the Civil 3D plugin against Civil 3D $($BuildC3D.Year)"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

# Copy the 5 Autodesk reference DLLs from THIS PC's Civil 3D (never shipped)
$refDir = Join-Path $SrcDir 'C_References'
foreach ($dll in @('accoremgd.dll', 'AcDbMgd.dll', 'acmgd.dll', 'AecBaseMgd.dll', 'AeccDbMgd.dll')) {
    $f = Get-ChildItem $BuildC3D.Dir -Recurse -Depth 2 -Filter $dll -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $f) { Add-Result 'Civil 3D references' 'FAIL' "$dll missing"; Stop-Install "$dll not found in $($BuildC3D.Dir)." }
    Copy-Item $f.FullName $refDir -Force
}
Write-Ok "Civil 3D reference DLLs copied from $($BuildC3D.Dir)"

# Which .NET does this Civil 3D run on? (2025 started on .NET 8; later
# updates of 2025/2026 moved to .NET 10.) Read it from AcDbMgd.dll.
$NetMajor = 8
try {
    $asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $refDir 'AcDbMgd.dll'))
    $sr = $asm.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'System.Runtime' } | Select-Object -First 1
    if ($sr -and $sr.Version.Major -ge 8) { $NetMajor = $sr.Version.Major }
} catch { Write-Info "Could not read the .NET version from AcDbMgd.dll; will detect it while building." }
Write-Ok "Civil 3D $($BuildC3D.Year) on this PC runs on .NET $NetMajor"

function Get-DotnetFor([int]$major) {
    # 1. a system-wide SDK of that major version
    $sys = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($sys) {
        $sdks = (Invoke-Native { & $sys.Source --list-sdks }) -join "`n"
        if ($sdks -match "(?m)^$major\.") { return $sys.Source }
    }
    # 2. our private copy
    $priv = Join-Path $DotnetDir 'dotnet.exe'
    if (Test-Path $priv) {
        $sdks = (Invoke-Native { & $priv --list-sdks }) -join "`n"
        if ($sdks -match "(?m)^$major\.") { return $priv }
    }
    # 3. install it into our folder (no admin)
    Write-Info "Installing the .NET $major SDK into $DotnetDir (no admin needed, ~250 MB) ..."
    $dps = Join-Path $WorkDir 'dotnet-install.ps1'
    if (-not (Test-Path $dps)) { try { Download 'https://dot.net/v1/dotnet-install.ps1' $dps } catch { return $null } }
    Invoke-Native { powershell -NoProfile -ExecutionPolicy Bypass -File $dps -Channel "$major.0" -InstallDir $DotnetDir -NoPath } | Select-Object -Last 3 | ForEach-Object { Write-Info $_ }
    if (Test-Path $priv) {
        $sdks = (Invoke-Native { & $priv --list-sdks }) -join "`n"
        if ($sdks -match "(?m)^$major\.") { return $priv }
    }
    return $null
}

$proj = Join-Path $SrcDir 'plugin\Civil3dMcpPlugin\Civil3dMcpPlugin.csproj'
$outDir = Join-Path $WorkDir 'plugin-out'
$pluginDll = Join-Path $outDir 'Civil3dMcpPlugin.dll'
$built = $false
for ($attempt = 1; $attempt -le 2 -and -not $built; $attempt++) {
    $dotnet = Get-DotnetFor $NetMajor
    if (-not $dotnet) { Add-Result ".NET $NetMajor SDK" 'FAIL' 'Not installed'; Stop-Install "Could not install the .NET $NetMajor SDK." }
    Write-Ok ".NET $NetMajor SDK: $dotnet"
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    Get-ChildItem (Split-Path $proj) -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    # Set the target .NET version inside the project file itself (more reliable
    # than overriding it on the command line), then restore + build separately.
    $projText = [IO.File]::ReadAllText($proj)
    $projText = [regex]::Replace($projText, '<TargetFramework>[^<]+</TargetFramework>', "<TargetFramework>net$NetMajor.0-windows</TargetFramework>")
    [IO.File]::WriteAllText($proj, $projText, (New-Object System.Text.UTF8Encoding($false)))
    Write-Info "Restoring packages and building for net$NetMajor.0-windows ..."
    $ro = Invoke-Native { & $dotnet restore $proj "-p:Civil3DReferencesPath=$refDir" }
    $re = $LASTEXITCODE
    $bo = @($ro)
    if ($re -eq 0) {
        $bo += Invoke-Native { & $dotnet build $proj -c Release --no-restore -o $outDir "-p:Civil3DReferencesPath=$refDir" }
        if ($LASTEXITCODE -eq 0 -and (Test-Path $pluginDll)) { $built = $true; break }
    }
    # Civil 3D needs a newer .NET than we guessed? The error says which one.
    $m = [regex]::Match(($bo -join "`n"), "uses 'System\.Runtime, Version=(\d+)\.")
    if ($m.Success -and [int]$m.Groups[1].Value -gt $NetMajor) {
        $NetMajor = [int]$m.Groups[1].Value
        Write-Warn2 "Civil 3D needs .NET $NetMajor - retrying the build for .NET $NetMajor ..."
        continue
    }
    $bo | Where-Object { $_ -match 'error' } | Select-Object -First 10 | ForEach-Object { Write-Info $_ }
    $bo | Select-Object -Last 5 | ForEach-Object { Write-Info $_ }
    Add-Result 'Plugin build' 'FAIL' 'dotnet build failed'
    Stop-Install "Could not build the Civil 3D plugin."
}
if (-not $built) { Add-Result 'Plugin build' 'FAIL' 'dotnet build failed'; Stop-Install "Could not build the Civil 3D plugin." }
Add-Result ".NET SDK" 'OK' ".NET $NetMajor"
# never ship the Autodesk DLLs inside the bundle
foreach ($dll in @('accoremgd.dll', 'AcDbMgd.dll', 'acmgd.dll', 'AecBaseMgd.dll', 'AeccDbMgd.dll')) { Remove-Item (Join-Path $outDir $dll) -Force -ErrorAction SilentlyContinue }
Write-Ok "Plugin built: Civil3dMcpPlugin.dll"
Add-Result 'Plugin build' 'OK' "Civil 3D $($BuildC3D.Year), .NET $NetMajor"

# ---------------------------------------------------------------------
# 5. INSTALL THE PLUGIN AS AN AUTO-LOADING BUNDLE
# ---------------------------------------------------------------------
Write-Step "5/7  Installing the plugin into Civil 3D (loads automatically)"
while (Get-Process -Name 'acad' -ErrorAction SilentlyContinue) {
    Write-Warn2 "Civil 3D is running. Save your drawings and close Civil 3D."
    $r = Read-Host "    Press Enter when Civil 3D is closed (or type S to skip this step)"
    if ($r.Trim().ToUpper() -eq 'S') { break }
}
if (Get-Process -Name 'acad' -ErrorAction SilentlyContinue) {
    Write-Warn2 "Skipped: Civil 3D is still open. Run this file again later."
    Add-Result 'Plugin bundle' 'WARN' 'Skipped (Civil 3D open)'
} else {
    if (Test-Path $BundleDir) { Move-Item $BundleDir (Join-Path $env:TEMP "Civil3dMcp.bundle.backup-$Stamp") -Force; Write-Info "Old bundle moved to %TEMP%" }
    $contents = Join-Path $BundleDir 'Contents'
    New-Item -ItemType Directory -Force -Path $contents | Out-Null
    Copy-Item (Join-Path $outDir '*') $contents -Recurse -Force
    Get-ChildItem $BundleDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    # Civil 3D 2025 = R25.0, 2026 = R25.1 (both .NET 8)
    $pc = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AppVersion="1.0.$InstallerVersion" ProductCode="{6F3C1E2A-9B7D-4C1E-8A5F-2D4B6C8E0A13}" UpgradeCode="{0B8D5E71-3C2F-4A9E-B6D1-7E4F2A9C5B30}" Name="Civil 3D MCP" Description="MCP bridge between AI apps and Civil 3D (github.com/barbosaihan/civil3d-mcp)" Author="Petrocon BIM" ProductType="Application" AutodeskProduct="Civil3D">
  <CompanyDetails Name="Petrocon BIM" />
  <Components Description="Civil 3D 2025 and 2026 (.NET 8/10)">
    <RuntimeRequirements OS="Win64" Platform="Civil3D" SeriesMin="R25.0" SeriesMax="R25.1" />
    <ComponentEntry AppName="Civil3dMcpPlugin" Version="1.0.$InstallerVersion" ModuleName="./Contents/Civil3dMcpPlugin.dll" AppDescription="Starts the MCP listener on port $Port" LoadOnAutoCADStartup="True" />
  </Components>
</ApplicationPackage>
"@
    [IO.File]::WriteAllText((Join-Path $BundleDir 'PackageContents.xml'), $pc, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "Bundle installed: $BundleDir"
    Add-Result 'Plugin bundle' 'OK' $BundleDir
}

# ---------------------------------------------------------------------
# 6. SERVER TEST (MCP handshake)
# ---------------------------------------------------------------------
Write-Step "6/7  Testing the MCP server"
$initIn = Join-Path $WorkDir 'mcp-test.txt'
$msgs = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"installer-check","version":"1.0"}}}' + "`n" +
        '{"jsonrpc":"2.0","method":"notifications/initialized"}' + "`n" +
        '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' + "`n"
[IO.File]::WriteAllText($initIn, $msgs, (New-Object System.Text.UTF8Encoding($false)))
$outF = Join-Path $WorkDir 'server-out.txt'; $errF = Join-Path $WorkDir 'server-err.txt'
$p = Start-Process -FilePath $nodeExe -ArgumentList "`"$ServerJs`"" -RedirectStandardInput $initIn -RedirectStandardOutput $outF -RedirectStandardError $errF -NoNewWindow -PassThru
$null = $p.Handle
if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch {} }
$outText = ''; if (Test-Path $outF) { $outText = [string](Get-Content $outF -Raw) }
$errText = ''; if (Test-Path $errF) { $errText = [string](Get-Content $errF -Raw) }
if ($outText -match 'civil3d_execute' -and $outText -match 'civil3d_query' -and $outText -match 'civil3d_skills') {
    Write-Ok "Server answered with its 3 tools: civil3d_execute, civil3d_query, civil3d_skills"
    Add-Result 'Server test' 'OK' 'handshake + tools/list'
} else {
    Write-Bad "The server did not answer correctly."
    ($errText -split "`n" | Select-Object -Last 10) | ForEach-Object { Write-Info $_ }
    Add-Result 'Server test' 'FAIL' 'See log'
    Stop-Install "Server test failed."
}

# ---------------------------------------------------------------------
# 7. CLAUDE DESKTOP
# ---------------------------------------------------------------------
Write-Step "7/7  Connecting Claude Desktop"
function Set-ClaudeConfig($path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $cfg = $null
    if (Test-Path $path) {
        Copy-Item $path "$path.backup-$Stamp" -Force
        $raw = Get-Content $path -Raw
        if ($raw -and $raw.Trim()) { try { $cfg = $raw | ConvertFrom-Json } catch { Write-Warn2 "Existing config is not valid JSON; backup kept: $path.backup-$Stamp" } }
    }
    if (-not $cfg) { $cfg = New-Object psobject }
    if (-not ($cfg.PSObject.Properties.Name -contains 'mcpServers') -or $null -eq $cfg.mcpServers) { $cfg | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue (New-Object psobject) -Force }
    $entry = [pscustomobject][ordered]@{
        command = $nodeExe
        args    = @($ServerJs)
        env     = [pscustomobject][ordered]@{ CIVIL3D_HOST = 'localhost'; CIVIL3D_PORT = "$Port" }
    }
    $cfg.mcpServers | Add-Member -NotePropertyName 'civil3d' -NotePropertyValue $entry -Force
    [IO.File]::WriteAllText($path, ($cfg | ConvertTo-Json -Depth 32), (New-Object System.Text.UTF8Encoding($false)))
    $null = (Get-Content $path -Raw | ConvertFrom-Json).mcpServers.civil3d.command
}
$configs = @(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
foreach ($pkg in $claudeStore) { $configs += Join-Path $pkg.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json' }
foreach ($cd in $claudeData)   { $configs += Join-Path $cd.FullName 'claude_desktop_config.json' }
$cfgOk = $true
foreach ($c in $configs) { try { Set-ClaudeConfig $c; Write-Ok "civil3d added to $c" } catch { $cfgOk = $false; Write-Bad "Could not update $c : $($_.Exception.Message)" } }
if ($cfgOk) { Add-Result 'Claude config' 'OK' "$($configs.Count) location(s)" } else { Add-Result 'Claude config' 'WARN' 'See log' }

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
    } catch { Write-Warn2 "Could not enable Developer Mode in $df" }
}
Write-Ok "Claude Developer Mode enabled"
Add-Result 'Claude Developer Mode' 'OK' ''

# Restart Claude
if ($claudeInstalled) {
    $running = Get-Process -Name 'claude' -ErrorAction SilentlyContinue
    if ($running -and (Ask-YesNo "Claude must restart to load Civil 3D MCP. Close and reopen Claude now?" 'Y')) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 3
    }
    if (-not (Get-Process -Name 'claude' -ErrorAction SilentlyContinue)) {
        try { if ($claudeApp) { Start-Process "shell:AppsFolder\$($claudeApp.AppID)" } elseif (Test-Path $claudeExe) { Start-Process $claudeExe }; Write-Ok "Claude Desktop started" } catch {}
    }
}

# ---------------------------------------------------------------------
# SUMMARY
# ---------------------------------------------------------------------
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
Write-Host "   1. Open Civil 3D and a drawing. The command line should say:"
Write-Host "      'Civil3D MCP plugin initialized.'  (check any time with C3DMCPSTATUS)"
Write-Host "   2. In Claude Desktop: Settings > Developer should show 'civil3d' as running."
Write-Host "   3. Start a NEW chat and ask:"
Write-Host "      'Use civil3d to list the surfaces, alignments and pipe networks in my drawing.'"
Write-Host "   Tip: save your drawing before asking Claude to change anything."
Write-Host ""
Write-Host "   If something fails: send this log file to support: $LogFile" -ForegroundColor Gray
try { Stop-Transcript | Out-Null } catch {}
if ($Results | Where-Object { $_.Status -eq 'FAIL' }) { exit 1 } else { exit 0 }
