# Publishes release\out\GMS Co-op.exe as a GitHub release of RArtutos/gms-coop, so every launcher
# offers the update by itself. Run make_release.ps1 first. Needs the gh CLI, logged in.
# Usage: powershell -ExecutionPolicy Bypass -File release\publish.ps1 [-Notes "what changed"]

param([string]$Notes = "")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$repo = 'RArtutos/gms-coop'
$exe = Join-Path $root 'release\out\GMS Co-op.exe'
$sha = "$exe.sha256"
$pluginCs = Join-Path $root 'GMSCoop\Plugin.cs'

if (-not (Test-Path $exe)) { throw "Run make_release.ps1 first: $exe not found" }
$m = Select-String -Path $pluginCs -Pattern 'const string Version = "(\d+\.\d+\.\d+)"'
if (-not $m) { throw "Plugin version not found in $pluginCs" }
$version = $m.Matches[0].Groups[1].Value
$tag = "v$version"

# The launcher in release\out must be the same version as the mod (make_release stamps it).
$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
if (-not $fileVersion.StartsWith($version)) { throw "release\out\GMS Co-op.exe is $fileVersion, the mod is $version: run make_release.ps1 again" }

# Hash file: the launcher refuses an update whose download does not match it.
$hash = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($sha, "$hash  GMS Co-op.exe`n", (New-Object Text.UTF8Encoding($false)))

$existing = gh release view $tag --repo $repo 2>$null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists in $repo. Bump the version (Plugin.cs) before publishing again." }

if ($Notes -eq "") { $Notes = "GMS Co-op $tag" }
gh release create $tag $exe $sha --repo $repo --title "GMS Co-op $tag" --notes $Notes --latest
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "Published $tag to https://github.com/$repo/releases/tag/$tag"
