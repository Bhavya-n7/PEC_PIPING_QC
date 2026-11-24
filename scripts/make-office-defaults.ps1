<#
  Writes office-defaults.json (the shared folder and the update folder of this company) from the private config.local.json.
  The file is git-ignored; the release build puts it next to the installer and inside the app, so server names never live in the repository.

    powershell -File scripts\make-office-defaults.ps1 [-Out <folder>]     # default: the repository root
#>
param([string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = $root }
$cfgFile = Join-Path $root 'config.local.json'
if (-not (Test-Path $cfgFile)) { throw 'config.local.json not found: copy config.example.json and fill it in.' }
$cfg = Get-Content $cfgFile -Raw | ConvertFrom-Json
if (-not $cfg.shared_root -or $cfg.shared_root -like '*<*') { throw 'shared_root is not set in config.local.json.' }
New-Item -ItemType Directory -Force $Out | Out-Null
# update_public_key (the PUBLIC half, made by scripts\new-release-key.ps1): every PC then refuses an update that is not signed with the release key
$json = [ordered]@{ SharedRoot = $cfg.shared_root; UpdateSource = $(if ($cfg.update_source) { $cfg.update_source } else { $null }); UpdatePublicKey = $(if ($cfg.update_public_key) { $cfg.update_public_key } else { $null }) } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $Out 'office-defaults.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
"Wrote $(Join-Path $Out 'office-defaults.json')"
