param(
  [string]$InstallRoot = "C:\publish\AzerothReforgedFiles", # local folder mirroring /files
  [string]$BaseUrl = "https://cdn.azerothreforged.xyz/files/",
  [string]$Out = "C:\publish\manifest\latest.json",
  [string]$Version = "1.0.0"
)

function Get-FileHashHex([string]$p){ (Get-FileHash -Algorithm SHA256 -Path $p).Hash.ToUpper() }

$files = @()
Get-ChildItem -Recurse $InstallRoot | Where-Object {!$_.PSIsContainer} | ForEach-Object {
  $rel = $_.FullName.Substring($InstallRoot.Length+1).Replace("\","/")
  $files += [pscustomobject]@{
    path = $rel
    size = $_.Length
    sha256 = Get-FileHashHex $_.FullName
  }
}

$manifest = [pscustomobject]@{
  version = $Version
  minLauncherVersion = "1.0.0"
  clientBuild = "3.3.5a-12340"
  baseUrl = $BaseUrl
  files = $files
}

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding UTF8
Write-Host "Manifest written to $Out"
