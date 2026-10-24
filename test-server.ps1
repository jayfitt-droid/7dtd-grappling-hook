# Local dedicated server for multiplayer testing (PLAN.md section 10).
#
#   test-server.ps1 start            start the server (unlisted, EAC off, its own save and data folder)
#   test-server.ps1 status           is it running, and the mod's lines from its log
#   test-server.ps1 cmd "<command>"  run a server console command over telnet, e.g. "lp" or "admin add <name> 0"
#   test-server.ps1 stop             save and shut down
#
# The server runs from the same v3.3 install as the game, so it loads the same
# Mods folder and deploy.ps1 updates both. Join from the game with
# Join a Game > Connect to IP > 127.0.0.1, port 26900.
param(
    [Parameter(Position = 0)][ValidateSet('start', 'status', 'cmd', 'stop')][string]$Action = 'status',
    [Parameter(Position = 1)][string]$Command = ''
)
$ErrorActionPreference = 'Stop'
$gameRoot = 'C:\7D2D\Custom\v3.3'
$dataRoot = Join-Path $gameRoot 'TestServerData'
$configPath = Join-Path $dataRoot 'serverconfig_test.xml'
$logPath = Join-Path $dataRoot 'output_log_server.txt'
$telnetPort = 8081
$serverPort = 26900

function Get-ServerProcess {
    Get-CimInstance Win32_Process -Filter "Name='7DaysToDie.exe'" |
        Where-Object { $_.CommandLine -match '-dedicated' }
}

function Write-TestConfig {
    # Start from the install's own serverconfig.xml so every setting the game
    # version expects is present, then change only what a local test needs.
    [xml]$xml = Get-Content -LiteralPath (Join-Path $gameRoot 'serverconfig.xml') -Raw
    $settings = [ordered]@{
        ServerName           = 'GrapplingHook Test'
        ServerPort           = "$serverPort"
        ServerVisibility     = '0'          # not listed anywhere
        ServerMaxPlayerCount = '4'
        EACEnabled           = 'false'      # code mods need EAC off on the server and every client
        ServerAllowCrossplay = 'false'
        WebDashboardEnabled  = 'false'
        TelnetEnabled        = 'true'
        TelnetPort           = "$telnetPort"
        TelnetPassword       = ''           # empty: telnet listens on this PC only
        GameWorld            = 'Navezgane'
        GameName             = 'GrapplingHookTest'
        UserDataFolder       = $dataRoot    # own saves and serveradmin.xml, apart from single-player
    }
    $root = $xml.DocumentElement
    foreach ($name in $settings.Keys) {
        $node = $root.SelectSingleNode("property[@name='$name']")
        if ($node -eq $null) {
            # UserDataFolder ships commented out.
            $node = $xml.CreateElement('property')
            $node.SetAttribute('name', $name)
            [void]$root.AppendChild($node)
        }
        $node.SetAttribute('value', $settings[$name])
    }
    $xml.Save($configPath)
}

function Invoke-Telnet([string]$text, [int]$waitMs = 1500) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $telnetPort)
    } catch {
        throw "Telnet on port $telnetPort is not answering. Is the server running and finished loading? ($($_.Exception.Message))"
    }
    try {
        $stream = $client.GetStream()
        $buffer = New-Object byte[] 65536
        $output = New-Object System.Text.StringBuilder
        $read = {
            param($ms)
            $until = [DateTime]::UtcNow.AddMilliseconds($ms)
            while ([DateTime]::UtcNow -lt $until) {
                if ($stream.DataAvailable) {
                    $n = $stream.Read($buffer, 0, $buffer.Length)
                    [void]$output.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $n))
                } else { Start-Sleep -Milliseconds 50 }
            }
        }
        & $read 700
        [void]$output.Clear()   # drop the welcome banner
        $bytes = [Text.Encoding]::UTF8.GetBytes($text + "`r`n")
        $stream.Write($bytes, 0, $bytes.Length)
        & $read $waitMs
        $bytes = [Text.Encoding]::UTF8.GetBytes("exit`r`n")
        try { $stream.Write($bytes, 0, $bytes.Length) } catch { }
        return $output.ToString().TrimEnd()
    } finally { $client.Close() }
}

switch ($Action) {
    'start' {
        if (Get-ServerProcess) { Write-Host 'The test server is already running.'; return }
        if (-not (Test-Path -LiteralPath (Join-Path $gameRoot '7DaysToDie.exe'))) { throw "Game install not found: $gameRoot" }
        New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
        Write-TestConfig
        if (Test-Path -LiteralPath $logPath) { Move-Item -LiteralPath $logPath -Destination "$logPath.prev" -Force }
        # What the game's startdedicated.bat sets before launching.
        Set-Content -LiteralPath (Join-Path $gameRoot 'steam_appid.txt') -Value '251570' -NoNewline -Encoding ascii
        $env:SteamAppId = '251570'
        $env:SteamGameId = '251570'
        $arguments = "-logfile `"$logPath`" -quit -batchmode -nographics -configfile=`"$configPath`" -dedicated"
        Start-Process -FilePath (Join-Path $gameRoot '7DaysToDie.exe') -ArgumentList $arguments -WorkingDirectory $gameRoot -WindowStyle Minimized
        Write-Host "Test server starting (no window). The first start builds the save and can take a few minutes."
        Write-Host "Log: $logPath"
        Write-Host "When 'status' shows the server is ready, join from the game: Connect to IP 127.0.0.1, port $serverPort."
    }
    'status' {
        $process = Get-ServerProcess
        Write-Host ('Server: ' + $(if ($process) { "running (process $($process.ProcessId))" } else { 'not running' }))
        $game = Get-CimInstance Win32_Process -Filter "Name='7DaysToDie.exe'" | Where-Object { $_.CommandLine -notmatch '-dedicated' }
        Write-Host ('Game:   ' + $(if ($game) { 'running' } else { 'not running' }))
        if (Test-Path -LiteralPath $logPath) {
            $lines = Get-Content -LiteralPath $logPath
            $ready = $lines | Select-String -SimpleMatch 'StartGame done' | Select-Object -Last 1
            Write-Host ('Ready:  ' + $(if ($ready) { 'yes' } else { 'not yet (or check the log)' }))
            Write-Host "Log:    $logPath"
            Write-Host ''
            Write-Host 'Mod lines and errors (last 25):'
            $lines | Select-String -Pattern '\[GrapplingHook\]|ERR |Exception' | Select-Object -Last 25 |
                ForEach-Object { Write-Host "  $($_.Line)" }
        } else { Write-Host 'No server log yet.' }
    }
    'cmd' {
        if (-not $Command) { throw 'Give the console command in quotes, for example: test-server.ps1 cmd "lp"' }
        Invoke-Telnet $Command
    }
    'stop' {
        if (-not (Get-ServerProcess)) { Write-Host 'The test server is not running.'; return }
        Invoke-Telnet 'shutdown' 3000 | Out-Null
        Write-Host 'Shutdown sent; the server saves and exits.'
    }
}
