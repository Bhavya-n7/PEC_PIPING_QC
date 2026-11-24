<#
  Publishes this release's bundled skills, prompts and example requirement pack to the shared office folder (the QC share).
  The installer and the app never overwrite files in the shared folder (people edit them there), so a release manager runs this after reviewing what changed.

    powershell -File scripts\publish-to-shared.ps1                    # report only; the folder comes from config.local.json (shared_root)
    powershell -File scripts\publish-to-shared.ps1 -Shared <folder>   # report only, explicit folder
    powershell -File scripts\publish-to-shared.ps1 -Apply             # copy; changed files are backed up as <file>.bak_<date-time>

  Never deletes anything (the share does not allow it for users, and a release should not remove what people added).
  The shared folder path is never printed into the repository: it comes from the private config.local.json or the -Shared parameter.
#>
param([string]$Shared, [switch]$Apply)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Shared) {
  $cfg = Join-Path $root 'config.local.json'
  if (Test-Path $cfg) { $Shared = (Get-Content $cfg -Raw | ConvertFrom-Json).shared_root }
}
if (-not $Shared) { throw 'No shared folder: pass -Shared <folder> or set shared_root in config.local.json.' }
if (-not (Test-Path $Shared)) { throw "Shared folder not reachable: $Shared" }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$map = @(
  @{ From = Join-Path $root 'skills';       To = Join-Path $Shared 'skills';       Filter = '*' },
  @{ From = Join-Path $root 'prompts';      To = Join-Path $Shared 'prompts';      Filter = '*' },
  @{ From = Join-Path $root 'requirements'; To = Join-Path $Shared 'requirements'; Filter = 'example.json' }
)
$new = 0; $changed = 0; $same = 0
foreach ($m in $map) {
  if (-not (Test-Path $m.From)) { continue }
  Get-ChildItem $m.From -Recurse -File -Filter $m.Filter | ForEach-Object {
    $rel = $_.FullName.Substring($m.From.Length).TrimStart('\')
    $dst = Join-Path $m.To $rel
    if (-not (Test-Path $dst)) {
      $new++; "NEW      $rel"
      if ($Apply) { New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; Copy-Item $_.FullName $dst }
    }
    elseif ((Get-FileHash $_.FullName).Hash -ne (Get-FileHash $dst).Hash) {
      $changed++; "CHANGED  $rel"
      if ($Apply) { Copy-Item $dst "$dst.bak_$stamp"; Copy-Item $_.FullName $dst -Force }
    }
    else { $same++ }
  }
}
"$new new, $changed changed, $same unchanged." + $(if ($Apply) { ' Applied.' } else { ' Report only: add -Apply to copy.' })
