# Builds the friends package: "GMS Co-op.exe" (launcher + installer with the mod embedded).
# Run after building GMSCoop (Release). Output: release\out\GMS Co-op.exe
# The installer gets the plugin's version (from Plugin.cs), so both always say the same.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$mod = Join-Path $root 'GMSCoop\bin\Release\net472\GMSCoop.dll'
$pluginCs = Join-Path $root 'GMSCoop\Plugin.cs'
$bepZip = Join-Path $root 'tools\bepinex.zip'
$installer = Join-Path $root 'Installer'
$stage = Join-Path $root 'release\stage'
$out = Join-Path $root 'release\out'

if (-not (Test-Path $mod)) { throw "Build GMSCoop first: $mod not found" }
if (-not (Test-Path $bepZip)) { throw "BepInEx 5 zip not found: $bepZip" }
if ((Get-Item $mod).LastWriteTime -lt (Get-Item $pluginCs).LastWriteTime) { throw "GMSCoop.dll is older than Plugin.cs: build GMSCoop (Release) first" }

$m = Select-String -Path $pluginCs -Pattern 'const string Version = "(\d+\.\d+\.\d+)"'
if (-not $m) { throw "Plugin version not found in $pluginCs" }
$version = $m.Matches[0].Groups[1].Value
Write-Host "GMS Co-op $version"

# Stage = BepInEx files + our plugin, laid out exactly as in the game folder.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Expand-Archive -Path $bepZip -DestinationPath $stage -Force
Remove-Item (Join-Path $stage 'changelog.txt') -ErrorAction SilentlyContinue
if (-not (Test-Path (Join-Path $stage 'BepInEx\core\BepInEx.dll'))) { throw "$bepZip is not a BepInEx 5 zip (BepInEx\core\BepInEx.dll missing)" }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'BepInEx\plugins') | Out-Null
Copy-Item $mod (Join-Path $stage 'BepInEx\plugins\GMSCoop.dll') -Force
# Selectable characters (CC0 glTF, see GMSCoop\avatars\LICENSES.md), loaded by the mod at runtime.
$avatarsDst = Join-Path $stage 'BepInEx\plugins\GMSCoop_avatars'
New-Item -ItemType Directory -Force -Path $avatarsDst | Out-Null
Copy-Item (Join-Path $root 'GMSCoop\avatars\*.glb') $avatarsDst -Force
Copy-Item (Join-Path $root 'GMSCoop\avatars\LICENSES.md') $avatarsDst -Force

# Zip with '/' separators (ZipFile.CreateFromDirectory on Windows PowerShell writes '\').
$payload = Join-Path $installer 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stageFull = (Resolve-Path $stage).Path.TrimEnd('\') + '\'
$zip = [System.IO.Compression.ZipFile]::Open($payload, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -Path $stage -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($stageFull.Length).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }

& 'C:\Program Files\dotnet\dotnet.exe' build $installer -c Release "-p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item (Join-Path $installer 'bin\Release\net48\GMS Co-op.exe') $out -Force
Remove-Item (Join-Path $out 'Instalar GMS Co-op.exe') -ErrorAction SilentlyContinue
Get-Item (Join-Path $out 'GMS Co-op.exe') | Select-Object Name, Length, LastWriteTime, @{ n = 'Version'; e = { $_.VersionInfo.ProductVersion } }
