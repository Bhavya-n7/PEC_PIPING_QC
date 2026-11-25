<#
  Run before every push. Fails (exit 1) if a tracked or staged text file contains site-specific or personal data:
  server names/IPs, UNC share paths, Windows user folders, e-mail addresses, keys/secrets.
  Extra patterns (this company's server name, domain, user ids) go in the private config.local.json under "leak_patterns" (never committed).

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\leak-scan.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$patterns = @(
  '\b(?!127\.0\.0\.1\b)(?!0\.0\.0\.0\b)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b(?<!\bVersion\s\d+\.\d+\.\d+\.\d+)',   # IPv4 addresses
  '\\\\[A-Za-z0-9_.-]+\\[A-Za-z0-9_$.-]+',                      # UNC paths \\server\share
  '(?i)[A-Z]:\\Users\\[A-Za-z0-9_.-]+',                          # C:\Users\<name>
  '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.(com|org|net|in|co)\b',     # e-mail addresses
  '(?i)(client_secret|api[_-]?key|password|token)\s*[:=]\s*["''][^"'']{6,}'   # secrets
)
# Documented fake values: version numbers that look like IPs, generic placeholders, and the fake data the privacy-filter tests feed on purpose.
# Only these exact literals are exempt; real server names and users come from config.local.json and are always caught.
$benign = '<(Assembly|File)Version>|\\\\server\\share|\\\\<server>|\\\\fileserver\\|C:\\Users\\x\\|10\.20\.30\.40|leak-scan:ignore'
$cfg = Join-Path $root 'config.local.json'
$realOnly = '(?!)'   # never matches until config.local.json adds patterns
if (Test-Path $cfg) {
  $extra = (Get-Content $cfg -Raw | ConvertFrom-Json).leak_patterns
  if ($extra) { $patterns += $extra; $realOnly = ($extra -join '|') }   # a line that has a real private value is never exempt
}

# files that are in git or staged (deleted ones skipped); binary files skipped
$files = @(git ls-files) + @(git diff --cached --name-only --diff-filter=AM) | Sort-Object -Unique | Where-Object { $_ -and (Test-Path $_) }
$allow = @('LICENSE', 'scripts/leak-scan.ps1', 'third-party/cli-anything/PUBLISHING.md')   # the last one is the upstream project's file (placeholder e-mail addresses), kept unmodified
$hits = 0
foreach ($f in $files) {
  if ($allow -contains ($f -replace '\\', '/')) { continue }
  if ($f -match '\.(png|ico|zip|exe|dll|pdf|nwd|jpg|xlsx|docx)$') { continue }
  foreach ($p in $patterns) {
    $m = Select-String -Path $f -Pattern $p -ErrorAction SilentlyContinue
    foreach ($x in $m) {
      if ($x.Line -match $benign -and $x.Line -notmatch $realOnly) { continue }
      $hits++; Write-Host ("{0}:{1}: {2}" -f $f, $x.LineNumber, $x.Line.Trim()) -ForegroundColor Red
    }
  }
}
if ($hits) { Write-Host "`n$hits possible leak(s). Fix or move them to config.local.json before pushing." -ForegroundColor Red; exit 1 }
Write-Host "leak scan clean ($($files.Count) files checked)" -ForegroundColor Green
