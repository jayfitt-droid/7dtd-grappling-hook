# Builds the mod and packs it for players: dist\GrapplingHook-<version>.zip
# contains one top-level folder, GrapplingHook, so unzipping it into the game's
# Mods folder puts every file where the game expects it.
#   powershell -ExecutionPolicy Bypass -File package.ps1
# The art (asset bundle and icons) is taken as it is in modlet\GrapplingHook;
# run build-assets.ps1 first if the model or textures changed.
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$modSource = Join-Path $repoRoot 'modlet\GrapplingHook'
$project = Join-Path $repoRoot 'src\GrapplingHook\GrapplingHook.csproj'
$modBinary = Join-Path $repoRoot 'build\GrapplingHook.dll'
$distDir = Join-Path $repoRoot 'dist'

# Compile. Warnings count as a failure: the build is kept at 0 warnings.
$buildOutput = & dotnet build $project -c Release 2>&1
if ($LASTEXITCODE -ne 0) { $buildOutput | Select-Object -Last 20; throw 'Build failed.' }
if (-not ($buildOutput | Select-String -SimpleMatch '0 Warning(s)' -Quiet)) {
    $buildOutput | Select-String -Pattern 'warning' | Select-Object -First 20
    throw 'Build has warnings.'
}
if (-not (Test-Path -LiteralPath $modBinary)) { throw "Build produced no DLL: $modBinary" }

# Everything a player needs must be present before packing.
$required = @(
    'ModInfo.xml',
    'Config\items.xml', 'Config\recipes.xml', 'Config\loot.xml', 'Config\Localization.csv',
    'Config\GrapplingHookConfig.json',
    'Resources\GrapplingHook.unity3d',
    'UIAtlases\ItemIconAtlas\ammoGrapplingHookScrap.png',
    'UIAtlases\ItemIconAtlas\ammoGrapplingHookIron.png',
    'UIAtlases\ItemIconAtlas\ammoGrapplingHookSteel.png'
)
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $modSource $file))) { throw "Missing mod file: $file" }
}

[xml]$modInfo = Get-Content -LiteralPath (Join-Path $modSource 'ModInfo.xml')
$version = $modInfo.xml.Version.value
if (-not $version) { throw 'ModInfo.xml has no Version value.' }

# File list: the modlet, the DLL, the README and the license, all under GrapplingHook/.
$entries = [ordered]@{}
Get-ChildItem -LiteralPath $modSource -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($modSource.Length + 1).Replace('\', '/')
    $entries["GrapplingHook/$relative"] = $_.FullName
}
$entries['GrapplingHook/GrapplingHook.dll'] = $modBinary
$entries['GrapplingHook/README.md'] = Join-Path $repoRoot 'README.md'
$entries['GrapplingHook/LICENSE'] = Join-Path $repoRoot 'LICENSE'

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$zipPath = Join-Path $distDir "GrapplingHook-$version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

# Written entry by entry with forward slashes; Compress-Archive in Windows
# PowerShell stores backslashes, which other unzip tools turn into odd names.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $entries.Keys) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $entries[$name], $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }

# Read the zip back and compare every entry with its source file.
$sha = [System.Security.Cryptography.SHA256]::Create()
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($zip.Entries.Count -ne $entries.Count) { throw "Zip has $($zip.Entries.Count) entries, expected $($entries.Count)." }
    foreach ($entry in $zip.Entries) {
        if (-not $entries.Contains($entry.FullName)) { throw "Unexpected zip entry: $($entry.FullName)" }
        $stream = $entry.Open()
        try { $packed = [BitConverter]::ToString($sha.ComputeHash($stream)) } finally { $stream.Dispose() }
        $source = (Get-FileHash -LiteralPath $entries[$entry.FullName] -Algorithm SHA256).Hash
        if ($packed.Replace('-', '') -ne $source) { throw "Zip entry differs from its source: $($entry.FullName)" }
    }
}
finally { $zip.Dispose() }

$size = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
Write-Host "Packaged GrapplingHook $version ($($entries.Count) files, $size MB, verified): $zipPath"
Write-Host 'Install: unzip into the game''s Mods folder, giving Mods\GrapplingHook\ModInfo.xml.'
