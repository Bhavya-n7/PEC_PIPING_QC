<#
  Writes third-party\mcp-installers\manifest.json: the list of MCP-server installers that Setup offers, each with the SHA-256 of its script (Setup refuses a script
  that has been changed). Run it again after replacing a script; the build runs it too. The scripts themselves are the team's own one-click installers and are
  bundled unmodified.

    powershell -File scripts\make-mcp-addon-manifest.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'third-party\mcp-installers'
function Get-ScriptHash($f) { (Get-FileHash (Join-Path $dir $f) -Algorithm SHA256).Hash.ToLowerInvariant() }
$items = @(
  [ordered]@{ id = 'navisworks'; title = 'Navisworks MCP (nwd-mcp and NavisCoord)'; file = 'Install-NavisworksMCP_2.cmd'
    description = 'Lets Claude browse a model open in Navisworks (properties, search sets, viewpoints) and coordinate clashes. Installs two Navisworks plug-ins and two MCP servers.'
    detect = @(@{ folder = '^Navisworks (Manage|Simulate) 20(2[2-9]|3\d)$' })
    source = 'github.com/bimwright/nwd-mcp (Apache-2.0), github.com/HorizunGroup/naviscoord-mcp (MIT)'; closesApps = $false }
  [ordered]@{ id = 'autocad'; title = 'AutoCAD MCP (dwg-mcp)'; file = 'Install-AutoCADMCP_2.cmd'
    description = 'Lets Claude read and work in drawings open in AutoCAD 2022-2027. Installs an AutoCAD plug-in and the dwg-mcp server.'
    detect = @(@{ folder = '^AutoCAD 20(2[2-9]|3\d)$' })
    source = 'github.com/bimwright/dwg-mcp (Apache-2.0)'; closesApps = $true }
  [ordered]@{ id = 'civil3d'; title = 'Civil 3D MCP'; file = 'Install-Civil3DMCP_3.cmd'
    description = 'Lets Claude work with a model open in Civil 3D 2025 or 2026. Builds the plug-in against this PC''s Civil 3D, installs Node.js and the .NET 8 SDK in your user folder (no administrator rights), and the MCP server.'
    detect = @(@{ folder = '^AutoCAD 20(2[5-9]|3\d)$'; contains = 'C3D' })
    source = 'github.com/barbosaihan/civil3d-mcp (MIT)'; closesApps = $true }
  [ordered]@{ id = 'plant3d'; title = 'Plant 3D MCP (dwg-mcp)'; file = 'PlantMCP.cmd'
    description = 'The same dwg-mcp server as the AutoCAD one, enabled inside AutoCAD Plant 3D 2022-2027.'
    detect = @(@{ folder = '^AutoCAD 20(2[2-9]|3\d)$'; contains = 'PLNT3D' })
    source = 'github.com/bimwright/dwg-mcp (Apache-2.0)'; closesApps = $true }
  [ordered]@{ id = 'revit'; title = 'Revit MCP (Model MCP)'; file = 'RevitMCP.cmd'
    description = 'Lets Claude work with a model open in Revit 2022-2027. Installs a Revit add-in, uv (Python package runner, user folder) and the MCP server, and sets user variables so uv trusts the Windows certificates behind an office firewall.'
    detect = @(@{ folder = '^Revit 20(2[2-9]|3\d)$' })
    source = 'github.com/sharafutdinovdi/revit-model-mcp (licence not stated in the installer: check before wide use)'; closesApps = $true }
)
foreach ($i in $items) { $i.sha256 = Get-ScriptHash $i.file }
$json = [ordered]@{ version = 1; note = 'Third-party MCP servers for Autodesk programs. Each script downloads its server from the source named here when it runs. The scripts are run unmodified; Setup checks their SHA-256 first.'; addons = $items } | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $dir 'manifest.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
"Wrote $(Join-Path $dir 'manifest.json') ($($items.Count) installers)"
