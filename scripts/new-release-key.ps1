<#
  Makes the release key pair used to sign updates (once, by the release manager).

    powershell -File scripts\new-release-key.ps1                       # private key to %USERPROFILE%\.pec-qc\release-signing-key.xml
    powershell -File scripts\new-release-key.ps1 -PrivateKeyFile <path>

  - The PRIVATE key stays on the release manager's PC (never on the share, never in the repository). Only that file lets anyone publish an update.
    Back it up somewhere safe: if it is lost, every PC must be reinstalled with a new public key.
  - The PUBLIC key is printed: put it in config.local.json as "update_public_key" and the private key's path as "update_signing_key", then rebuild the installer
    (scripts\build-installer.ps1 writes the public key into office-defaults.json). From then on every PC refuses an update whose latest.json is not signed with this key.
  An existing key is never replaced (use -Force to make a new pair, which means reinstalling every PC).
#>
param([string]$PrivateKeyFile, [switch]$Force)
$ErrorActionPreference = 'Stop'
if (-not $PrivateKeyFile) { $PrivateKeyFile = Join-Path $env:USERPROFILE '.pec-qc\release-signing-key.xml' }
if ((Test-Path $PrivateKeyFile) -and -not $Force) { throw "A key already exists at $PrivateKeyFile. Use -Force only if you really want a new one (every PC must then be reinstalled)." }
New-Item -ItemType Directory -Force (Split-Path $PrivateKeyFile -Parent) | Out-Null
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
[System.IO.File]::WriteAllText($PrivateKeyFile, $rsa.ToXmlString($true))
# only this user may read the private key
icacls $PrivateKeyFile /inheritance:r /grant:r "$($env:USERNAME):(F)" | Out-Null
Write-Host "Private key written to $PrivateKeyFile (readable by you only). Keep it OFF the share and back it up."
Write-Host ''
Write-Host 'Public key (put this in config.local.json as "update_public_key", on one line):'
Write-Host $rsa.ToXmlString($false)
