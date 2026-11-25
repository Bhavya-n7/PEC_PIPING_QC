<# : batch portion - this file is a .cmd that runs the PowerShell body below
@echo off
setlocal
title Petrocon - Plant 3D MCP Installer
set "PETROCON_SCRIPT=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ([IO.File]::ReadAllText($env:PETROCON_SCRIPT))"
echo.
echo Finished. Scroll up to read the results, then press any key to close this window.
pause >nul
exit /b
#>

# ---------------------------------------------------------------------------
# PlantMCP.cmd - Plant 3D MCP installer (Windows PowerShell 5.1, ASCII only, normal user)
# Uses dwg-mcp (same server as the AutoCAD installer) and enables it inside AutoCAD Plant 3D 2022-2027
# Installs dwg-mcp by Bimwright (Apache-2.0): AutoCAD 2022-2027 plugin + MCP server
#   Plugin : %APPDATA%\Autodesk\ApplicationPlugins\Bimwright.Dwg.bundle
#   Server : %LOCALAPPDATA%\Bimwright\dwg-mcp\server\current\dwg-mcp.exe
#   Claude : "dwg-mcp" added to all three Claude config locations
# Log: %TEMP%\PlantMCP-install-<date>-<time>.log
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
Remove-TypeData System.Array -ErrorAction SilentlyContinue
$Build = '2026-11-23'
$FallbackTag  = 'v2.1.1'
$RepoUrl      = 'https://github.com/bimwright/dwg-mcp'
$ServerName   = 'dwg-mcp'
$ServerExe    = Join-Path $env:LOCALAPPDATA 'Bimwright\dwg-mcp\server\current\dwg-mcp.exe'
$BundleDir    = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\Bimwright.Dwg.bundle'
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Log   = Join-Path $env:TEMP "PlantMCP-install-$Stamp.log"
$Work  = Join-Path $env:TEMP "PlantMCP-$Stamp"
$script:StepNo = 0
$StepTotal = 7

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
    return $code
}

function Backup-File([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Copy-Item -LiteralPath $Path -Destination "$Path.bak-$Stamp" -Force
        Info "backup: $Path.bak-$Stamp"
    }
}

# ----------------------------- Banner --------------------------------------
Write-Host ''
Write-Host '  =====================================================================' -ForegroundColor Cyan
Write-Host '   Petrocon Engineers & Consultants - BIM Department' -ForegroundColor Cyan
Write-Host '   Plant 3D MCP Installer  (dwg-mcp for AutoCAD Plant 3D 2022-2027)' -ForegroundColor Cyan
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
Write-LogLine "PlantMCP installer (build $Build) started on $env:COMPUTERNAME as $env:USERNAME"

# ----------------------------- 1. Checks -----------------------------------
Step 'Checking this computer'
try {
    $os = Get-CimInstance Win32_OperatingSystem
    $arch = if ([Environment]::Is64BitOperatingSystem) { '64-bit' } else { '32-bit' }
    if ($arch -ne '64-bit') { Fail "$($os.Caption) is 32-bit. Plant 3D MCP needs 64-bit Windows." }
    Ok "$($os.Caption) ($arch)"
} catch { Fail "Could not read Windows version: $_" }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'Running as administrator. Close this window and double-click the file normally (not "Run as administrator").'
}
Ok "Running as normal user: $env:USERNAME"

# Smart App Control blocks unsigned add-in DLLs
$sac = $null
try { $sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction Stop).VerifiedAndReputablePolicyState } catch {}
switch ("$sac") {
    '1' { Fail 'Smart App Control is ON. It blocks the AutoCAD plugin DLLs. Ask IT to turn it off, then run this again.' }
    '2' { Warn 'Smart App Control is in Evaluation mode. If Windows switches it ON, the AutoCAD plugin will be blocked.' }
    '0' { Ok 'Smart App Control: Off' }
    default { Ok 'Smart App Control: not present on this Windows' }
}

# AutoCAD 2022-2027 (any AutoCAD-based product: AutoCAD, Civil 3D, Map 3D, Plant 3D ...)
$YearSeries = [ordered]@{ 2022 = 'R24.1'; 2023 = 'R24.2'; 2024 = 'R24.3'; 2025 = 'R25.0'; 2026 = 'R25.1'; 2027 = 'R26.0' }
$found = @()
foreach ($year in $YearSeries.Keys) {
    $key = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($YearSeries[$year])"
    $products = @()
    $exeOk = $false
    if (Test-Path $key) {
        foreach ($sub in Get-ChildItem $key -ErrorAction SilentlyContinue) {
            $p = Get-ItemProperty $sub.PSPath -ErrorAction SilentlyContinue
            if ($p.AcadLocation -and (Test-Path (Join-Path $p.AcadLocation 'acad.exe'))) {
                $exeOk = $true
                if ($p.ProductName) { $products += $p.ProductName }
            }
        }
    }
    if (Test-Path (Join-Path $env:ProgramFiles "Autodesk\AutoCAD $year\acad.exe")) { $exeOk = $true }
    if ($exeOk) {
        $label = if ($products.Count) { ($products | Select-Object -Unique) -join ', ' } else { "AutoCAD $year" }
        $found += [pscustomobject]@{ Year = $year; Products = $label }
        Ok "Found $year : $label"
    }
}
if ($found.Count -eq 0) {
    # Show what IS there, to help support
    $other = @(Get-ChildItem (Join-Path $env:ProgramFiles 'Autodesk') -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    if ($other.Count) { Info ("Autodesk folders on this PC: " + ($other -join ', ')) }
    Fail "AutoCAD Plant 3D 2022-2027 was not found (checked the registry and $env:ProgramFiles\Autodesk). Plant 3D 2021 and older are not supported by dwg-mcp."
}
# Plant 3D installs into 'AutoCAD <year>' with a PLNT3D folder; its registry ProductName contains 'Plant'
$plant = @($found | Where-Object { $_.Products -match 'Plant' -or (Test-Path (Join-Path $env:ProgramFiles "Autodesk\AutoCAD $($_.Year)\PLNT3D")) })
if ($plant.Count -eq 0) {
    Fail ('AutoCAD-based software was found (' + (($found | ForEach-Object { $_.Products }) -join ', ') + '), but not AutoCAD Plant 3D 2022-2027. For plain AutoCAD or Civil 3D use the AutoCAD installer instead.')
}
foreach ($pp in $plant) { Ok "AutoCAD Plant 3D $($pp.Year) found" }
# Plant 3D only loads bundles marked AutoCAD* (the vendor ships plain "AutoCAD"), so always enable it
$verticals = @($plant | ForEach-Object { [pscustomobject]@{ Year = $_.Year; Products = "AutoCAD Plant 3D $($_.Year)" } })

# Claude Desktop
$claudeSeen = (Test-Path "$env:LOCALAPPDATA\AnthropicClaude") -or (Test-Path "$env:APPDATA\Claude") -or
              @(Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue).Count -gt 0
if ($claudeSeen) { Ok 'Claude Desktop found' } else { Warn 'Claude Desktop not found. Install it from claude.ai/download; the settings will be ready for it.' }

# Internet
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try { $null = Invoke-WebRequest -Uri $RepoUrl -UseBasicParsing -Method Head -TimeoutSec 20; Ok 'Reachable: GitHub (dwg-mcp release page)' }
catch { Fail "Cannot reach GitHub ($RepoUrl): $($_.Exception.Message)" }

# ----------------------------- 2. Download ---------------------------------
Step 'Downloading the latest dwg-mcp release (web page, not API)'
New-Item -ItemType Directory -Path $Work -Force | Out-Null
$tag = $null
try {
    $r = Invoke-WebRequest -Uri "$RepoUrl/releases/latest" -UseBasicParsing -TimeoutSec 30
    $final = $r.BaseResponse.ResponseUri.AbsoluteUri
    if ($final -match '/releases/tag/(v\d+\.\d+\.\d+)') { $tag = $Matches[1] }
    elseif ($r.Content -match '/bimwright/dwg-mcp/releases/tag/(v\d+\.\d+\.\d+)') { $tag = $Matches[1] }
} catch { Warn "Could not read the release page: $($_.Exception.Message)" }
if (-not $tag) { $tag = $FallbackTag; Warn "Using the tested release $tag" } else { Ok "Latest release: $tag" }

$zipName = "DwgMcp.Setup-$tag-win-x64.zip"
$dl      = Join-Path $Work 'download'
New-Item -ItemType Directory -Path $dl -Force | Out-Null
$zipPath = Join-Path $dl $zipName
$shaPath = "$zipPath.sha256"
try {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri "$RepoUrl/releases/download/$tag/$zipName" -OutFile $zipPath -UseBasicParsing -TimeoutSec 600
    Invoke-WebRequest -Uri "$RepoUrl/releases/download/$tag/$zipName.sha256" -OutFile $shaPath -UseBasicParsing -TimeoutSec 60
} catch { Fail "Download failed: $($_.Exception.Message)" }
$expected = ((Get-Content -LiteralPath $shaPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual   = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Info "expected SHA-256: $expected"
Info "actual   SHA-256: $actual"
if ($expected -ne $actual) { Fail 'Checksum does not match the published one. The download is damaged or was changed; nothing was installed.' }
Ok ("Downloaded and verified {0} ({1:N0} MB)" -f $zipName, ((Get-Item $zipPath).Length / 1MB))

# ----------------------------- 3. Close apps -------------------------------
Step 'Closing AutoCAD and Claude'
$acad = @(Get-Process -Name 'acad' -ErrorAction SilentlyContinue)
$claude = @(Get-Process -Name 'claude' -ErrorAction SilentlyContinue)
if ($acad.Count -or $claude.Count) {
    if ($acad.Count) { Write-Host '  AutoCAD is open: SAVE YOUR DRAWINGS NOW.' -ForegroundColor Yellow }
    if ($claude.Count) { Write-Host '  Claude is open: it will be closed and reopened by you at the end.' -ForegroundColor Yellow }
    Write-Host '  Press Enter when ready, and the installer will close them: ' -ForegroundColor Yellow -NoNewline
    [void](Read-Host)
    if (@(Get-Process -Name 'acad' -ErrorAction SilentlyContinue).Count) {
        Get-Process -Name 'acad' -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.CloseMainWindow() }
        Start-Sleep -Seconds 8
        if (@(Get-Process -Name 'acad' -ErrorAction SilentlyContinue).Count) {
            Fail 'AutoCAD is still open (maybe a "Save changes?" box). Close AutoCAD yourself, then run this again.'
        }
    }
    Get-Process -Name 'claude' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}
Ok 'AutoCAD and Claude are closed'

# ----------------------------- 4. Install ----------------------------------
Step 'Installing the AutoCAD plugin and the MCP server'
$setup = Join-Path $Work 'setup'
New-Item -ItemType Directory -Path $setup -Force | Out-Null
try { Expand-Archive -LiteralPath $zipPath -DestinationPath $setup -Force } catch { Fail "Could not extract the setup ZIP: $_" }
Get-ChildItem -LiteralPath $setup -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
$man = Get-Content -LiteralPath (Join-Path $setup 'manifest.json') -Raw | ConvertFrom-Json
Info "package version $($man.version), AutoCAD years in package: $(@($man.packedAutocadYears) -join ', ')"
foreach ($f in $found) { if (@($man.packedAutocadYears) -notcontains $f.Year) { Warn "AutoCAD $($f.Year) is not in this package and will not get the plugin." } }

Backup-File (Join-Path $BundleDir 'PackageContents.xml')
$installArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $setup 'install.ps1'),
                 '-Client', 'none', '-SetupZip', $zipPath, '-ReleaseTag', $tag)
Info 'running the vendor installer (install.ps1 -Client none -SetupZip ... -ReleaseTag ...)'
$code = Invoke-Native 'powershell.exe' $installArgs
if ($code -ne 0) { Fail "The dwg-mcp installer stopped (exit code $code). Its messages are in the log." }
if (-not (Test-Path -LiteralPath $ServerExe)) { Fail "Server not found after install: $ServerExe" }
if (-not (Test-Path -LiteralPath (Join-Path $BundleDir 'PackageContents.xml'))) { Fail "Plugin not found after install: $BundleDir" }
Get-ChildItem -LiteralPath $BundleDir -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
Ok "Plugin installed: $BundleDir"
Ok "Server installed: $ServerExe"

# Civil 3D / Map 3D / Plant 3D etc. only load bundles marked AutoCAD* (vendor ships "AutoCAD" = plain AutoCAD only)
if ($verticals.Count) {
    $pc = Join-Path $BundleDir 'PackageContents.xml'
    $xmlText = [IO.File]::ReadAllText($pc)
    if ($xmlText -match 'Platform="AutoCAD"') {
        Backup-File $pc
        $xmlText = $xmlText -replace 'Platform="AutoCAD"', 'Platform="AutoCAD*"'
        [IO.File]::WriteAllText($pc, $xmlText, (New-Object System.Text.UTF8Encoding($true)))
        Ok ("Plugin enabled for AutoCAD-based products: " + (($verticals | ForEach-Object { $_.Products }) -join ', '))
        Warn 'dwg-mcp is tested by its developer on plain AutoCAD. In Plant 3D, use it read-only first; it edits the drawing (lines, blocks, text, attributes), not the Plant 3D project database.'
    }
}

# ----------------------------- 5. Handshake --------------------------------
Step 'Testing the server (real MCP handshake)'
function Read-Reply($Proc, [int]$Id, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($Proc.HasExited) { return $null }
        $task = $Proc.StandardOutput.ReadLineAsync()
        while (-not $task.Wait(500)) {
            if ((Get-Date) -ge $deadline -or $Proc.HasExited) { return $null }
        }
        $line = $task.Result
        if ($null -eq $line) { return $null }
        if (-not $line.Trim().StartsWith('{')) { continue }
        try { $msg = $line | ConvertFrom-Json } catch { continue }
        if ($msg.PSObject.Properties['id'] -and "$($msg.id)" -eq "$Id") { return $msg }
    }
    return $null
}
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ServerExe
$psi.Arguments = ''
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$hs = @{ Ok = $false; Tools = 0; Name = ''; Err = '' }
try {
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.StandardInput.NewLine = "`n"
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"petrocon-installer","version":"1"}}}')
    $p.StandardInput.Flush()
    $r0 = Read-Reply $p 0 90
    if ($r0 -and $r0.result) {
        $hs.Name = "$($r0.result.serverInfo.name) $($r0.result.serverInfo.version)"
        $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}')
        $p.StandardInput.Flush()
        $r1 = Read-Reply $p 1 60
        if ($r1 -and $r1.result) { $hs.Ok = $true; $hs.Tools = @($r1.result.tools).Count }
    }
    try { $p.StandardInput.Close() } catch {}
    if (-not $p.WaitForExit(5000)) { try { $p.Kill() } catch {} }
    if ($errTask.Wait(5000)) { $hs.Err = $errTask.Result }
} catch { $hs.Err = "$_" }
foreach ($l in ("$($hs.Err)" -split "`r?`n")) { if ($l) { Add-Content -LiteralPath $Log -Value "        stderr: $l" } }
if (-not $hs.Ok) { Fail 'The server did not answer the MCP handshake. Details are in the log.' }
Ok "Server answered: $($hs.Name), $($hs.Tools) tools"

# ----------------------------- 6. Claude config ----------------------------
Step 'Adding dwg-mcp to Claude (all three settings locations)'
$cfgFiles = New-Object System.Collections.Generic.List[string]
$cfgFiles.Add("$env:APPDATA\Claude\claude_desktop_config.json")
Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
    ForEach-Object { $cfgFiles.Add((Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json')) }
Get-ChildItem "$env:LOCALAPPDATA\Claude-Data" -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { $cfgFiles.Add((Join-Path $_.FullName 'claude_desktop_config.json')) }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$written = 0
foreach ($f in $cfgFiles) {
    $dir = Split-Path $f
    # Roaming location is always written; Store / Claude-Data only when that Claude exists
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
    $entry = [pscustomobject]@{ command = $ServerExe; args = @() }
    if ($cfg.mcpServers.PSObject.Properties[$ServerName]) { $cfg.mcpServers.$ServerName = $entry }
    else { $cfg.mcpServers | Add-Member -NotePropertyName $ServerName -NotePropertyValue $entry }
    $json = $cfg | ConvertTo-Json -Depth 32
    try { [void]($json | ConvertFrom-Json) } catch { Warn "New JSON did not validate, left unchanged: $f"; continue }
    [IO.File]::WriteAllText($f, $json, $utf8NoBom)
    $others = @($cfg.mcpServers.PSObject.Properties.Name | Where-Object { $_ -ne $ServerName })
    Ok ("Updated: $f" + $(if ($others.Count) { "  (kept: " + ($others -join ', ') + ")" } else { '' }))
    $written++
}
if ($written -eq 0) { Fail 'Could not write any Claude settings file.' }

# Developer Mode (shows local MCP servers under Settings > Developer)
$devFile = "$env:APPDATA\Claude\developer_settings.json"
try {
    $dev = [pscustomobject]@{}
    if (Test-Path -LiteralPath $devFile) { Backup-File $devFile; $dev = [IO.File]::ReadAllText($devFile) | ConvertFrom-Json; if ($null -eq $dev) { $dev = [pscustomobject]@{} } }
    if ($dev.PSObject.Properties['allowDevTools']) { $dev.allowDevTools = $true } else { $dev | Add-Member -NotePropertyName 'allowDevTools' -NotePropertyValue $true }
    [IO.File]::WriteAllText($devFile, ($dev | ConvertTo-Json -Depth 8), $utf8NoBom)
    Ok 'Developer Mode enabled'
} catch { Warn "Could not enable Developer Mode: $_" }

# ----------------------------- 7. Done -------------------------------------
Step 'Finished'
Ok "Plant 3D MCP ($tag) is installed and answers."
Write-Host ''
Write-Host '  Next steps:' -ForegroundColor White
Write-Host '   1. Open Claude. Settings > Developer should show dwg-mcp as running.' -ForegroundColor White
Write-Host '   2. Open Plant 3D with a drawing (choose "Always Load" if asked). The plugin loads by itself.' -ForegroundColor White
Write-Host '   3. In a Claude chat linked to this PC, ask: "Which Plant 3D drawing is open?"' -ForegroundColor White
Write-Host '      (read-only first - save your drawing before asking for any change).' -ForegroundColor White
Write-Host '   4. To undo: run uninstall.ps1 from the setup folder below, and restore the .bak files listed in the log.' -ForegroundColor White
Info "setup folder kept at: $setup"
Write-Host ''
Write-Host "  Log file: $Log" -ForegroundColor Cyan
Write-LogLine 'Completed successfully'
