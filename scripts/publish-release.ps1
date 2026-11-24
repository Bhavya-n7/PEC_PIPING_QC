<#
  Publishes the newest build (dist\) to an update folder, which is what the app's "Update now" and the automatic update read.
  The default folder is "update_source" of the private config.local.json (the updates folder of the shared office folder).

    powershell -File scripts\publish-release.ps1                          # report only
    powershell -File scripts\publish-release.ps1 -Apply                   # copy
    powershell -File scripts\publish-release.ps1 -To <folder> -Apply      # another folder (for example a test folder)

  Run scripts\build-installer.ps1 first. An older version is never published over a newer one.
  Order matters: the payload and the installer are copied first and latest.json LAST, so a PC that sees the new latest.json always finds the files.
  Nothing is ever deleted (the share does not allow it for users, and a release must not remove what is there).
#>
param([switch]$Apply, [string]$To, [string]$SigningKey)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$files = 'payload.zip', 'PecQcSetup.exe', 'latest.json'
$cfgPath = Join-Path $root 'config.local.json'
if (-not $SigningKey -and (Test-Path $cfgPath)) { $SigningKey = (Get-Content $cfgPath -Raw | ConvertFrom-Json).update_signing_key }
$office0 = Join-Path $dist 'office-defaults.json'
$clientsCheck = (Test-Path $office0) -and ((Get-Content $office0 -Raw | ConvertFrom-Json).UpdatePublicKey)
if ($clientsCheck -and -not ($SigningKey -and (Test-Path $SigningKey))) { throw 'This build tells every PC to require a signed release, but no signing key was given (-SigningKey <file> or update_signing_key in config.local.json): PCs would ignore the update.' }
foreach ($f in $files) { if (-not (Test-Path (Join-Path $dist $f))) { throw "dist\$f is missing: run scripts\build-installer.ps1 first." } }
if (-not $To) {
  $cfg = Join-Path $root 'config.local.json'
  if (Test-Path $cfg) { $To = (Get-Content $cfg -Raw | ConvertFrom-Json).update_source }
}
if (-not $To) { throw 'No target: pass -To <folder> or set update_source in config.local.json.' }
$new = [version]((Get-Content (Join-Path $dist 'latest.json') -Raw | ConvertFrom-Json).Version)
$cur = Join-Path $To 'latest.json'
if (Test-Path $cur) {
  $old = [version]((Get-Content $cur -Raw | ConvertFrom-Json).Version)
  if ($old -gt $new) { throw "SKIP: $To already has $old, newer than $new." }
  if ($old -eq $new) { Write-Host "Note: $To already has version $old; it will be replaced." }
}
$office = Join-Path $dist 'office-defaults.json'
foreach ($f in $files + $(if (Test-Path $office) { 'office-defaults.json' } else { @() })) {
  "{0,-8} {1}  <- dist\{2}" -f $(if (Test-Path (Join-Path $To $f)) { 'REPLACE' } else { 'NEW' }), (Join-Path $To $f), $f
}
if ($Apply) {
  New-Item -ItemType Directory -Force $To | Out-Null
  if (Test-Path $office) { Copy-Item $office $To -Force }   # Setup reads its defaults from next to itself
  Copy-Item (Join-Path $dist 'payload.zip') $To -Force
  Copy-Item (Join-Path $dist 'PecQcSetup.exe') $To -Force
  Copy-Item (Join-Path $dist 'latest.json') $To -Force
  if ($SigningKey -and (Test-Path $SigningKey)) {
    # sign exactly the bytes that were copied; a PC that sees the new latest.json before its signature simply ignores it until the signature arrives
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $To 'latest.json'))
    $rsa = New-Object System.Security.Cryptography.RSACng; $rsa.FromXmlString([System.IO.File]::ReadAllText($SigningKey))
    $sig = [Convert]::ToBase64String($rsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss))
    [System.IO.File]::WriteAllText((Join-Path $To 'latest.json.sig'), $sig)
    Write-Host 'latest.json signed (latest.json.sig).'
  }
}
"Version $new. " + $(if ($Apply) { 'Published.' } else { 'Report only: add -Apply to copy.' })
