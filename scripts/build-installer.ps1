<#
  Builds the installer package into dist\ :
    dist\PecQcSetup.exe        the Setup window, one self-contained exe
    dist\payload.zip           the app + MCP server + skills + prompts + help, self-contained: no .NET or Python needed on the user's PC
    dist\latest.json           release file for the in-app update check
    dist\office-defaults.json  (only when config.local.json names the shared folder) the shared and update folders of this company

  Run from the repository root:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-installer.ps1
  Optional:   -Version 0.1.1     builds that version instead of the one in Directory.Build.props (used to test the update path)
  Needs the .NET 10 SDK. The version is the one in Directory.Build.props; the notes are the matching "## <version>" section of CHANGELOG.md.
#>
param([string]$Version)
$ErrorActionPreference = 'Stop'
# a per-user .NET SDK (installed without administrator rights) lives in the user profile and may not be on PATH in this shell
$hasSdk = $false; if (Get-Command dotnet -ErrorAction SilentlyContinue) { $hasSdk = [bool](dotnet --list-sdks 2>$null) }   # a runtime-only dotnet is not enough
if (-not $hasSdk) {
  $userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
  if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) { $env:PATH = "$userDotnet;$env:PATH"; $env:DOTNET_ROOT = $userDotnet } else { throw 'The .NET 10 SDK was not found (install it, or put dotnet on PATH).' }
}
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version }
$vp = "-p:Version=$Version"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Write-Host "Building version $Version"

Write-Host '1/5 publish the assistant app'
dotnet publish "$root\src\Pec.Qc.Desktop" -c Release -r win-x64 --self-contained $vp -o "$stage\app" | Out-Null
if ($LASTEXITCODE) { throw 'publish of the app failed' }

Write-Host '2/5 publish the MCP server (a folder: the tool compiler needs the framework files)'
$mcpDir = Join-Path $stage 'app\mcp'
if (Test-Path $mcpDir) { Remove-Item $mcpDir -Recurse -Force }
dotnet publish "$root\src\Pec.Qc.Mcp" -c Release -r win-x64 --self-contained -p:PublishSingleFile=false $vp -o $mcpDir | Out-Null
if ($LASTEXITCODE) { throw 'publish of the MCP server failed' }
Get-ChildItem $mcpDir -Include *.pdb -Recurse | Remove-Item -Force
foreach ($need in 'mcp\Pec.Qc.Mcp.exe', 'skills', 'prompts', 'help\help.md') { if (-not (Test-Path (Join-Path "$stage\app" $need))) { throw "the package is missing $need" } }

# the Navisworks export tool (net48, built against the Navisworks installed on THIS build PC; the Navisworks DLLs themselves are never copied into the package)
$navisDir = Get-ChildItem "$env:ProgramW6432\Autodesk" -Directory -Filter 'Navisworks Manage*' -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'Autodesk.Navisworks.Automation.dll') } | Sort-Object Name -Descending | Select-Object -First 1
if ($navisDir) {
  Write-Host "2b/5 Navisworks export tool (built against $($navisDir.Name))"
  foreach ($proj in 'Pec.Qc.NavisExport', 'Pec.Qc.NavisRunner') { dotnet build "$root\src\$proj" -c Release $vp -p:NavisDir="$($navisDir.FullName)" | Out-Null; if ($LASTEXITCODE) { throw "build of $proj failed" } }
  $navisOut = Join-Path $stage 'app\navis'; New-Item -ItemType Directory -Force $navisOut | Out-Null
  Copy-Item "$root\src\Pec.Qc.NavisRunner\bin\Release\net48\Pec.Qc.NavisRunner.exe*" $navisOut
  Copy-Item "$root\src\Pec.Qc.NavisExport\bin\Release\net48\PecQcExporter.dll" $navisOut
  foreach ($need in 'Pec.Qc.NavisRunner.exe', 'PecQcExporter.dll') { if (-not (Test-Path (Join-Path $navisOut $need))) { throw "the package is missing navis\$need" } }
} else { Write-Warning 'Navisworks Manage is not installed on this PC: the package has NO model export tool (model_export will say so). Build on a PC with Navisworks to include it.' }

# the MCP servers for the Autodesk programs are installed by the program itself (Settings > Install MCP servers, and Setup): nothing is bundled here

Write-Host '3/5 office defaults and documentation'
foreach ($f in 'README.md', 'CHANGELOG.md') { if (Test-Path "$root\$f") { Copy-Item "$root\$f" "$stage\app" } }
$cfg = Join-Path $root 'config.local.json'
$hasOffice = $false
if (Test-Path $cfg) {
  $c = Get-Content $cfg -Raw | ConvertFrom-Json
  if ($c.shared_root -and $c.shared_root -notlike '*<*') {
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\make-office-defaults.ps1" -Out $dist | Out-Null
    Copy-Item (Join-Path $dist 'office-defaults.json') "$stage\app\office-defaults.json"
    $hasOffice = $true
  }
}
Write-Host ($(if ($hasOffice) { 'office defaults written (shared folder and update folder)' } else { 'no shared_root in config.local.json: this build has no office defaults' }))

Write-Host '4/5 zip the payload'
Compress-Archive -Path "$stage\app\*" -DestinationPath "$dist\payload.zip" -CompressionLevel Optimal

Write-Host '5/5 publish Setup (single file)'
dotnet publish "$root\src\Pec.Qc.Setup" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true $vp -o "$stage\setup" | Out-Null
if ($LASTEXITCODE) { throw 'publish of Setup failed' }
Copy-Item "$stage\setup\PecQcSetup.exe" $dist

# release file for the update check
$notes = ''
$cl = Join-Path $root 'CHANGELOG.md'
if (Test-Path $cl) {
  $text = Get-Content $cl -Raw
  $m = [regex]::Match($text, '(?s)## ' + [regex]::Escape($Version) + '[^\r\n]*\r?\n(.*?)(\r?\n## |\z)')
  if ($m.Success) { $notes = $m.Groups[1].Value.Trim() } else { Write-Warning "CHANGELOG.md has no '## $Version' section: the release notes are empty." }
}
# the SHA-256 of both files that Setup and the update copy: the release file is signed by publish-release.ps1, so these cannot be changed on the share
$setupHash = (Get-FileHash (Join-Path $dist 'PecQcSetup.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
$payloadHash = (Get-FileHash (Join-Path $dist 'payload.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
@{ Version = $Version; Notes = $notes; Setup = 'PecQcSetup.exe'; Released = (Get-Date).ToString('s'); SetupSha256 = $setupHash; PayloadSha256 = $payloadHash } | ConvertTo-Json | Set-Content (Join-Path $dist 'latest.json') -Encoding UTF8

Remove-Item $stage -Recurse -Force
Get-ChildItem $dist | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
