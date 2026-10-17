param(
    [ValidateSet('Start', 'Stop')][string]$Action = 'Start',
    [switch]$NoBrowser
)

# Windows PowerShell 5.1 compatible. Process ownership is verified before every stop.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$runtime = Join-Path $root 'artifacts\launcher'
$stateFile = Join-Path $runtime 'processes.json'
$site = 'http://127.0.0.1:5173'
$api = 'http://127.0.0.1:5080/api/v1'
$state = @{}
$lockFile = $null
$http = $null
$started = @()

function Save-State {
    $temp = Join-Path $runtime 'processes.tmp'
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $stateFile -Force
}

function Find-Tool([string]$name, [string[]]$candidates) {
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return [IO.Path]::GetFullPath($candidate) }
    }
    $command = Get-Command "$name.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw "Не найден $name. Подробности подготовки проекта — в README.md."
}

function Owned-Process([string]$name) {
    if (-not $state.ContainsKey($name)) { return $null }
    $entry = $state[$name]
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$entry.pid)" -ErrorAction Stop
    if (-not $process) { $state.Remove($name); Save-State; return $null }
    $created = $process.CreationDate.ToUniversalTime().ToString('o')
    $target = if ($name -eq 'api') { Join-Path $root 'server\TradeBot.Api\bin\Debug\net10.0\TradeBot.Api.dll' } else { Join-Path $root 'node_modules\vite\bin\vite.js' }
    $expectedName = if ($name -eq 'api') { 'dotnet.exe' } else { 'node.exe' }
    if ($entry.root -ne $root -or $created -ne $entry.created -or
        $process.ExecutablePath -ne $entry.executable -or $process.CommandLine -cne $entry.commandLine -or
        $process.Name -ne $expectedName -or -not $process.CommandLine.Contains('"' + $target + '"')) {
        # A stale PID must never authorize terminating a different process.
        $state.Remove($name); Save-State
        throw "Процесс $name изменился. Он не будет остановлен. Повторите запуск после проверки порта."
    }
    return $process
}

function Port-Owner([int]$port) {
    return @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
}

function Request-Local([string]$url, [string]$method = 'GET') {
    $message = New-Object System.Net.Http.HttpRequestMessage
    $message.Method = New-Object System.Net.Http.HttpMethod($method)
    $message.RequestUri = [Uri]$url
    $message.Headers.Add('X-TradeBot-Client', 'web')
    try {
        $response = $http.SendAsync($message).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) { throw "Локальный сервер ответил $([int]$response.StatusCode)." }
            return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        } finally { $response.Dispose() }
    } finally { $message.Dispose() }
}

function Wait-Ready([string]$name, [int]$port, [string]$url) {
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process = Owned-Process $name
        if (-not $process) { throw "$name завершился при запуске. Проверьте журналы в $runtime." }
        $owners = @(Port-Owner $port)
        if ($owners.Count -gt 0 -and @($owners | Where-Object { $_ -ne $process.ProcessId }).Count -gt 0) {
            throw "Порт $port занят другим процессом. Чужой процесс не будет остановлен."
        }
        if ($owners -contains $process.ProcessId) {
            try {
                $body = Request-Local $url
                if ($name -eq 'api' -and ($body | ConvertFrom-Json).status -ne 'ok') { throw 'Сервер ещё не готов.' }
                return
            } catch { }
        }
        Start-Sleep -Milliseconds 400
    }
    throw "$name не стал доступен за 40 секунд. Проверьте журналы в $runtime."
}

function Start-Owned([string]$name, [string]$executable, [string]$arguments, [string]$workingDirectory) {
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $workingDirectory -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runtime "$name.log") -RedirectStandardError (Join-Path $runtime "$name-error.log")
    $details = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
    if (-not $details) { throw "$name не запустился. Проверьте журналы в $runtime." }
    $state[$name] = @{ pid = $process.Id; created = $details.CreationDate.ToUniversalTime().ToString('o'); executable = $details.ExecutablePath; commandLine = $details.CommandLine; root = $root }
    Save-State
}

function Stop-Owned([string]$name) {
    $process = Owned-Process $name
    if ($process) { Stop-Process -Id $process.ProcessId -ErrorAction Stop }
    $state.Remove($name); Save-State
}

try {
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    try { $lockFile = [IO.File]::Open((Join-Path $runtime 'launcher.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'Запуск или остановка уже выполняется. Дождитесь закрытия предыдущего окна.' }
    if (Test-Path -LiteralPath $stateFile) {
        $loaded = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        foreach ($property in $loaded.PSObject.Properties) { if ($property.Name -in @('api', 'web')) { $state[$property.Name] = $property.Value } }
    }
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    $http = New-Object System.Net.Http.HttpClient($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(3)

    if ($Action -eq 'Stop') {
        $server = Owned-Process 'api'
        if ($server) {
            $owners = @(Port-Owner 5080)
            if ($owners -notcontains $server.ProcessId -or @($owners | Where-Object { $_ -ne $server.ProcessId }).Count -gt 0) {
                throw 'Не удалось подтвердить сервер на порту 5080. Автоматическая остановка отменена.'
            }
            # A running data recording is finished properly so its file is complete.
            try { $scan = (Request-Local "$api/scanner") | ConvertFrom-Json } catch { $scan = $null }
            if ($scan -and $scan.state -in @('starting', 'scanning')) {
                Write-Host 'Завершаем запись данных...'
                Request-Local "$api/scanner/$($scan.id)/stop" 'POST' | Out-Null
                $deadline = [DateTime]::UtcNow.AddSeconds(45)
                do {
                    Start-Sleep -Milliseconds 500
                    $scan = (Request-Local "$api/scanner") | ConvertFrom-Json
                } while ($scan.state -in @('starting', 'scanning') -and [DateTime]::UtcNow -lt $deadline)
                if ($scan.state -in @('starting', 'scanning')) { throw 'Запись данных ещё сохраняется. Сервер оставлен работающим; повторите остановку позже.' }
            }
        }
        Stop-Owned 'web'
        Stop-Owned 'api'
        if (@(Port-Owner 5080).Count -gt 0 -or @(Port-Owner 5173).Count -gt 0) {
            Write-Host 'Процессы этого запуска остановлены. Порты заняты другими процессами — они оставлены работающими.' -ForegroundColor Yellow
        } else {
            Write-Host 'Trade Bot остановлен. Сохранённые записи и настройки остались на диске.' -ForegroundColor Green
        }
    } else {
        # Check both ports before starting or building anything.
        foreach ($service in @(@{ name = 'api'; port = 5080 }, @{ name = 'web'; port = 5173 })) {
            $owned = Owned-Process $service.name
            $owners = @(Port-Owner $service.port)
            if ($owners.Count -gt 0 -and (-not $owned -or @($owners | Where-Object { $_ -ne $owned.ProcessId }).Count -gt 0)) {
                throw "Порт $($service.port) уже занят процессом, запущенным другим способом. Закройте соответствующее окно сервера или сайта через Ctrl+C. Затем повторите запуск."
            }
        }
        $node = Find-Tool 'node' @((Join-Path $root '.tools\node\node.exe'), (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'))
        $nodeVersion = [version]((& $node --version).Trim().TrimStart('v'))
        if ($nodeVersion -lt [version]'22.18.0') { throw 'Нужен Node.js 22.18 или новее.' }
        $dotnet = Find-Tool 'dotnet' @((Join-Path $root '.tools\dotnet\dotnet.exe'))
        $vite = Join-Path $root 'node_modules\vite\bin\vite.js'
        if (-not (Test-Path -LiteralPath $vite)) { throw 'Не установлены зависимости сайта. Сначала подготовьте проект по разделу «Запуск и проверка» в README.md.' }
        if (-not (Owned-Process 'api')) {
            Write-Host 'Подготавливаем сервер...'
            & $dotnet build (Join-Path $root 'server\TradeBot.Api\TradeBot.Api.csproj') --nologo *> (Join-Path $runtime 'build.log')
            if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать сервер. Подробности: $runtime\build.log" }
            $assembly = Join-Path $root 'server\TradeBot.Api\bin\Debug\net10.0\TradeBot.Api.dll'
            # Housekeeping here only compresses finished recordings and copies the database daily; nothing is deleted
            # (Storage:RetentionDays stays 0).
            Start-Owned 'api' $dotnet ('"' + $assembly + '" --Api:Port=5080 --Storage:Housekeeping=true --Lab:AutoProcess=true') (Join-Path $root 'server\TradeBot.Api')
            $started += 'api'
        }
        Wait-Ready 'api' 5080 "$api/health"
        if (-not (Owned-Process 'web')) {
            Write-Host 'Запускаем сайт...'
            # Explicit proxy avoids inheriting a test-server override from the caller.
            $oldProxy = $env:TRADE_BOT_API_URL
            try {
                $env:TRADE_BOT_API_URL = 'http://127.0.0.1:5080'
                Start-Owned 'web' $node ('"' + $vite + '" --host 127.0.0.1 --port 5173 --strictPort') $root
            } finally { $env:TRADE_BOT_API_URL = $oldProxy }
            $started += 'web'
        }
        Wait-Ready 'web' 5173 $site
        Write-Host "Trade Bot работает: $site" -ForegroundColor Green
        Write-Host 'Это окно можно закрыть. Для остановки используйте «Остановить Trade Bot.cmd».'
        if (-not $NoBrowser) { Start-Process $site }
    }
} catch {
    # Roll back only processes created by this invocation. Existing sessions are preserved.
    if ($Action -eq 'Start') {
        foreach ($name in @('web', 'api')) {
            if ($started -contains $name) { try { Stop-Owned $name } catch { Write-Warning $_.Exception.Message } }
        }
    }
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
} finally {
    if ($http) { $http.Dispose() }
    if ($lockFile) { $lockFile.Dispose() }
}
