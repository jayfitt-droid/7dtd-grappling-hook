# Second player on this PC for multiplayer tests (PLAN.md section 10).
#
#   test-client2.ps1        mirror the test mods and start the Steam copy of the game
#   test-client2.ps1 -NoStart   only mirror the mods
#
# The Steam install runs with its own user data folder, so it loads the same
# mods as the v3.3 install and the test server (not the AppData modpack), and
# its saves and settings stay apart. DevLink and HotReload are left out: they
# are development tools, and two copies of DevLink fight over one port.
# Join with Connect to IP 127.0.0.1, port 26900.
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'
$steamGame = 'D:\Games\Steam\steamapps\common\7 Days To Die'
$testMods = 'C:\7D2D\Custom\v3.3\Mods'
$dataRoot = 'C:\7D2D\Custom\v3.3\TestClient2Data'
$modsRoot = Join-Path $dataRoot 'Mods'
$logPath = Join-Path $dataRoot 'output_log_client2.txt'
$skip = @('0_TFP_Harmony', 'DevLink', 'HotReload')   # the Steam copy has its own Harmony

if (-not (Test-Path -LiteralPath (Join-Path $steamGame '7DaysToDie.exe'))) { throw "Steam copy not found: $steamGame" }
$running = Get-CimInstance Win32_Process -Filter "Name='7DaysToDie.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($steamGame, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'The Steam copy is already running; close it first so its mods can be replaced.' }

New-Item -ItemType Directory -Force -Path $modsRoot | Out-Null
# Replace only folders this script put there (same names as in the test Mods folder).
$sources = Get-ChildItem -LiteralPath $testMods -Directory | Where-Object { $skip -notcontains $_.Name }
foreach ($old in Get-ChildItem -LiteralPath $modsRoot -Directory) {
    if ($old.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to replace a linked folder: $($old.FullName)" }
    Remove-Item -LiteralPath $old.FullName -Recurse -Force
}
foreach ($mod in $sources) { Copy-Item -LiteralPath $mod.FullName -Destination (Join-Path $modsRoot $mod.Name) -Recurse -Force }
$dll = Join-Path $modsRoot 'GrapplingHook\GrapplingHook.dll'
$same = (Get-FileHash $dll).Hash -eq (Get-FileHash (Join-Path $testMods 'GrapplingHook\GrapplingHook.dll')).Hash
Write-Host ("Mirrored " + (($sources | ForEach-Object Name) -join ', ') + " into $modsRoot (GrapplingHook.dll matches: $same).")
if ($NoStart) { return }

if (Test-Path -LiteralPath $logPath) { Move-Item -LiteralPath $logPath -Destination "$logPath.prev" -Force }
$arguments = "-userdatafolder=`"$dataRoot`" -noeac -skipintro -skipnewsscreen=true -logfile `"$logPath`""
Start-Process -FilePath (Join-Path $steamGame '7DaysToDie.exe') -ArgumentList $arguments -WorkingDirectory $steamGame
Write-Host "Steam copy starting as the second player. Log: $logPath"
