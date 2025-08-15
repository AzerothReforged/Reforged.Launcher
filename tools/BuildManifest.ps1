# Requires PowerShell 5+ (for Compress-Archive)
[CmdletBinding()]
param(
  # Root that mirrors your CDN (contains files/, Launcher/, and the JSON files)
  [string]$Root               = "R:\Manifest",

  # CDN base (no trailing slash)
  [string]$CdnBase            = "https://cdn.azerothreforged.xyz",

  # -------- Game manifest metadata --------
  [string]$GameVersion        = "1.0.0",
  [string]$ClientBuild        = "3.3.5a-12340",
  [string]$MinLauncherVersion = "1.0.0",

  # -------- Launcher update channel --------
  [string]$LauncherVersion    = "1.0.0",
  [ValidateSet("zip","exe")]
  [string]$LauncherPackage    = "zip",                         # zip = package entire Launcher folder; exe = single file
  [string]$LauncherExeName    = "AzerothReforged.Launcher.exe" # used when LauncherPackage=exe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FileHashHex([string]$Path) {
  (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToUpper()
}

# Paths that mirror your CDN
$FilesDir         = Join-Path $Root "files"
$LauncherDir      = Join-Path $Root "Launcher"

# Output JSON files (root)  ->  https://cdn.../latest.json and https://cdn.../launcher.json
$GameManifestOut  = Join-Path $Root "latest.json"
$LauncherJsonOut  = Join-Path $Root "launcher.json"

# Base URLs used in the manifests
$FilesBaseUrl     = "$CdnBase/files/"
$LauncherBaseUrl  = "$CdnBase/Launcher/"

if (-not (Test-Path $FilesDir))    { throw "Game files dir not found: $FilesDir" }
if (-not (Test-Path $LauncherDir)) { throw "Launcher dir not found: $LauncherDir" }

Write-Host "Root:          $Root"
Write-Host "CDN Base:      $CdnBase"
Write-Host "Files dir:     $FilesDir"
Write-Host "Launcher dir:  $LauncherDir"
Write-Host ""

# ------------------------------
# 1) Build GAME manifest
# ------------------------------
Write-Host "Building game manifest (latest.json)..." -ForegroundColor Cyan

# Exclusions for the game manifest (regex on forward-slash relative paths)
$excludePatterns = @(
  '^Data/[a-z]{2}[A-Z]{2}/realmlist\.wtf$',
  '^Launcher/.*',                       # never include launcher files in game manifest
  '^AzerothReforged\.Launcher\.exe$'    # safety if someone dropped it at files\ root
)

function Should-Exclude([string]$relPath) {
  foreach ($rx in $excludePatterns) {
    if ($relPath -match $rx) { return $true }
  }
  return $false
}

$files = @()
Get-ChildItem -Recurse $FilesDir | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
  $rel = $_.FullName.Substring($FilesDir.Length).TrimStart('\').Replace('\','/')
  if (Should-Exclude $rel) { return }
  $files += [pscustomobject]@{
    path   = $rel
    size   = $_.Length
    sha256 = Get-FileHashHex $_.FullName
  }
}

$gameManifest = [pscustomobject]@{
  version            = $GameVersion
  minLauncherVersion = $MinLauncherVersion
  clientBuild        = $ClientBuild
  baseUrl            = $FilesBaseUrl
  files              = $files
}

$null = New-Item -ItemType Directory -Force -Path $Root
$gameManifest | ConvertTo-Json -Depth 6 | Set-Content -Path $GameManifestOut -Encoding UTF8
Write-Host "Wrote: $GameManifestOut  ($($files.Count) files)" -ForegroundColor Green
Write-Host ""

# ------------------------------
# 2) Build LAUNCHER manifest
# ------------------------------
Write-Host "Building launcher update manifest (launcher.json)..." -ForegroundColor Cyan

# Determine target artifact (zip or exe)
if ($LauncherPackage -eq "zip") {
  $zipName = "AR-Launcher-$LauncherVersion.zip"
  $zipPath = Join-Path $LauncherDir $zipName

  # Clean old zip with same name
  if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

  # Build a staging list that excludes launcher.cfg (preserve user setting)
  $tempStage = Join-Path ([IO.Path]::GetTempPath()) ("ARLauncherStage_" + [Guid]::NewGuid())
  New-Item -ItemType Directory -Force -Path $tempStage | Out-Null

  # Mirror LauncherDir -> tempStage, excluding launcher.cfg (robocopy handles ACLs and speed)
  $null = & robocopy $LauncherDir $tempStage /E /XF launcher.cfg /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
  if ($LASTEXITCODE -gt 8) { throw "Robocopy failed (code $LASTEXITCODE) while staging launcher." }

  # Zip staged content
  Compress-Archive -Path (Join-Path $tempStage '*') -DestinationPath $zipPath -Force
  Remove-Item $tempStage -Recurse -Force

  $artifactPath = $zipPath
  $artifactUrl  = "$LauncherBaseUrl$zipName"
}
else {
  $exePath = Join-Path $LauncherDir $LauncherExeName
  if (-not (Test-Path $exePath)) { throw "Launcher EXE not found for packaging: $exePath" }
  $artifactPath = $exePath
  $artifactUrl  = "$LauncherBaseUrl$LauncherExeName"
}

$artifactSha = Get-FileHashHex $artifactPath

$launcherManifest = [pscustomobject]@{
  version = $LauncherVersion
  url     = $artifactUrl
  sha256  = $artifactSha
}

$launcherManifest | ConvertTo-Json -Depth 6 | Set-Content -Path $LauncherJsonOut -Encoding UTF8
Write-Host "Wrote: $LauncherJsonOut" -ForegroundColor Green
Write-Host "Launcher artifact: $artifactPath"
Write-Host "SHA256: $artifactSha"
Write-Host ""

Write-Host "Done." -ForegroundColor Cyan
