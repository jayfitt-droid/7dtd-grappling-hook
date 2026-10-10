# Rebuilds the mod's art: Blender models and bakes into the Unity project,
# then Unity builds the materials, prefab, inventory icon and asset bundle into
# modlet/GrapplingHook. Run deploy.ps1 afterwards to install.
#   powershell -File build-assets.ps1 [-SkipBlender]
param([switch]$SkipBlender)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
# Must match the game's engine (7DaysToDie_Data UnityPlayer.dll: 2022.3.62f2).
$unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe'
$project = Join-Path $repoRoot 'unity\GrapplingHookAssets'
$logDir = Join-Path $repoRoot 'unity'

if (-not $SkipBlender) {
    if (-not (Test-Path -LiteralPath $blender)) { throw "Blender not found: $blender" }
    $blenderLog = Join-Path $logDir 'blender.log'
    & $blender -b --factory-startup -P (Join-Path $repoRoot 'unity\blender\build_assets.py') -- `
        (Join-Path $project 'Assets\Source') (Join-Path $logDir 'preview') *> $blenderLog
    if ($LASTEXITCODE -ne 0 -or -not (Select-String -Path $blenderLog -Pattern '\[assets\] done' -Quiet)) {
        throw "Blender build failed; see $blenderLog"
    }
    Write-Host "Blender assets built (previews in unity\preview)."
}

if (-not (Test-Path -LiteralPath $unity)) { throw "Unity 2022.3.62f2 not found: $unity" }
$unityLog = Join-Path $logDir 'unity.log'
$proc = Start-Process -FilePath $unity -Wait -PassThru -NoNewWindow -ArgumentList @(
    '-batchmode', '-projectPath', "`"$project`"", '-executeMethod', 'GrapplingHookBuild.BuildAll',
    '-logFile', "`"$unityLog`"")
if ($proc.ExitCode -ne 0) { throw "Unity build failed (exit $($proc.ExitCode)); see $unityLog" }
Select-String -Path $unityLog -Pattern '\[GrapplingHookBuild\]' | ForEach-Object { $_.Line }
