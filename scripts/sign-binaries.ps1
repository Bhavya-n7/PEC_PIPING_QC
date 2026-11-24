<#
  Signs the executables of a build with an Authenticode code-signing certificate (Windows then shows the company name instead of "unknown publisher", and
  SmartScreen / antivirus products treat the files better). Run after scripts\build-installer.ps1 and BEFORE publish-release.ps1, because the SHA-256 of the signed
  files must be the ones in latest.json: this script therefore rewrites dist\latest.json's hashes too.

    powershell -File scripts\sign-binaries.ps1 -Thumbprint <thumbprint>            # certificate in the CurrentUser or LocalMachine "My" store
    powershell -File scripts\sign-binaries.ps1 -PfxFile <file> -PfxPassword <pw>   # certificate file
    -TimestampUrl <url>   timestamp server (default: DigiCert); the signature then stays valid after the certificate expires
    -NoTimestamp          sign without a timestamp (only for tests: the signature then expires with the certificate)
    -Check                only report whether dist\ is signed

  What it signs: dist\PecQcSetup.exe, and inside dist\payload.zip: PecQcAssistant.exe, mcp\Pec.Qc.Mcp.exe, navis\Pec.Qc.NavisRunner.exe, navis\PecQcExporter.dll
  (the zip is unpacked, signed and packed again). Needs a real certificate from a certificate authority for the SmartScreen benefit: a self-signed one only proves
  that the files were not changed. The certificate (and its password) never goes into the repository.
#>
param([string]$Thumbprint, [string]$PfxFile, [string]$PfxPassword, [string]$TimestampUrl = 'http://timestamp.digicert.com', [string]$Dist, [switch]$Check, [switch]$NoTimestamp)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dist) { $Dist = Join-Path $root 'dist' }
$names = 'PecQcAssistant.exe', 'mcp\Pec.Qc.Mcp.exe', 'navis\Pec.Qc.NavisRunner.exe', 'navis\PecQcExporter.dll'

function Report($file) {
  $s = Get-AuthenticodeSignature $file
  "{0,-52} {1,-14} {2}" -f (Split-Path $file -Leaf), $s.Status, $(if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { '' })
}

$setup = Join-Path $Dist 'PecQcSetup.exe'; $zip = Join-Path $Dist 'payload.zip'
foreach ($f in $setup, $zip) { if (-not (Test-Path $f)) { throw "$f is missing: run scripts\build-installer.ps1 first." } }

if ($Check) {
  Report $setup
  $tmp = Join-Path $env:TEMP ('pecqc-sigcheck-' + [guid]::NewGuid().ToString('N')); Expand-Archive $zip $tmp
  foreach ($n in $names) { if (Test-Path (Join-Path $tmp $n)) { Report (Join-Path $tmp $n) } }
  Remove-Item $tmp -Recurse -Force; return
}

if ($PfxFile) {
  $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxFile, $PfxPassword)
} elseif ($Thumbprint) {
  $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
  if (-not $cert) { throw "No certificate with thumbprint $Thumbprint in the CurrentUser or LocalMachine store." }
} else { throw 'Give -Thumbprint or -PfxFile (see the header of this script).' }
if (-not $cert.HasPrivateKey) { throw 'The certificate has no private key.' }
if ($cert.EnhancedKeyUsageList.FriendlyName -notcontains 'Code Signing') { Write-Warning 'The certificate is not marked for Code Signing.' }

function Sign($file) {
  $args2 = @{ FilePath = $file; Certificate = $cert; HashAlgorithm = 'SHA256' }
  if (-not $NoTimestamp -and $TimestampUrl) { $args2.TimestampServer = $TimestampUrl }
  $r = Set-AuthenticodeSignature @args2
  if ($r.Status -in 'HashMismatch', 'NotSigned', 'UnknownError' -and $r.StatusMessage -notmatch 'not trusted|not valid for|chain') { throw "Signing $file failed: $($r.Status) $($r.StatusMessage)" }
  if (-not $r.SignerCertificate) { throw "Signing $file failed: no signature was written ($($r.Status))." }
}

# the payload: unpack, sign the executables, pack again
$tmp = Join-Path $env:TEMP ('pecqc-sign-' + [guid]::NewGuid().ToString('N'))
Expand-Archive $zip $tmp
foreach ($n in $names) { $f = Join-Path $tmp $n; if (Test-Path $f) { Sign $f; "signed  $n" } else { Write-Warning "not in the payload: $n" } }
Remove-Item $zip -Force
Compress-Archive -Path "$tmp\*" -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $tmp -Recurse -Force
Sign $setup; 'signed  PecQcSetup.exe'

# the release file names the hashes of the files as they are NOW
$lj = Join-Path $Dist 'latest.json'
if (Test-Path $lj) {
  $j = Get-Content $lj -Raw | ConvertFrom-Json
  $j.SetupSha256 = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
  $j.PayloadSha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  $j | ConvertTo-Json | Set-Content $lj -Encoding UTF8
  'latest.json: SHA-256 of Setup and payload updated'
}
Report $setup
