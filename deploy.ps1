# Build first, then deploy the mod and (if absent) the official Harmony loader.
$ErrorActionPreference = 'Stop'
$gameRoot = 'C:\7D2D\Custom\v3.3'
$repoRoot = $PSScriptRoot
$modsRoot = Join-Path $gameRoot 'Mods'
$stageRoot = Join-Path $gameRoot 'ModConfig\Modlets'
$modSource = Join-Path $repoRoot 'modlet\GrapplingHook'
$modBinary = Join-Path $repoRoot 'build\GrapplingHook.dll'
$loaderSource = Join-Path $stageRoot '0_TFP_Harmony'

if (-not (Test-Path -LiteralPath (Join-Path $gameRoot '7DaysToDie.exe'))) { throw "Game install not found: $gameRoot" }
if (-not (Test-Path -LiteralPath $modBinary)) { throw "Build the DLL first: $modBinary" }
if (-not (Test-Path -LiteralPath $modSource)) { throw "Modlet source missing: $modSource" }
if (-not (Test-Path -LiteralPath $loaderSource)) { throw "Harmony loader missing: $loaderSource" }

function Assert-DirectChild([string]$parent, [string]$child) {
    $expected = [IO.Path]::GetFullPath((Join-Path $parent 'GrapplingHook'))
    $actual = [IO.Path]::GetFullPath($child)
    if (-not [string]::Equals($expected, $actual, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace unexpected directory: $actual"
    }
    return $actual
}

New-Item -ItemType Directory -Force -Path $modsRoot | Out-Null
$loaderDestination = Join-Path $modsRoot '0_TFP_Harmony'
if (-not (Test-Path -LiteralPath $loaderDestination)) {
    Copy-Item -LiteralPath $loaderSource -Destination $loaderDestination -Recurse -Force
}

$modDestination = Assert-DirectChild $modsRoot (Join-Path $modsRoot 'GrapplingHook')
$stageDestination = Assert-DirectChild $stageRoot (Join-Path $stageRoot 'GrapplingHook')
foreach ($target in @($modDestination, $stageDestination)) {
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to replace a linked directory: $target"
        }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    Copy-Item -LiteralPath $modSource -Destination $target -Recurse -Force
    Copy-Item -LiteralPath $modBinary -Destination $target -Force
}

Write-Host "Installed GrapplingHook at $modDestination"
Write-Host "Staged GrapplingHook at $stageDestination"
