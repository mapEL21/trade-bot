param(
    [string]$Server = '',
    [string]$User = 'root',
    [int]$Port = 22,
    [switch]$Ask
)

# Builds Trade Bot for a Linux server (no .NET or Node needed there) and, with -Server, uploads and installs it over SSH.
# Windows PowerShell 5.1 compatible. Nothing secret is stored: SSH asks for its own password or uses your key.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$release = Join-Path $root 'artifacts\server-release'
$archive = Join-Path $root 'artifacts\trade-bot-server.tar.gz'

function Find-Tool([string]$name, [string[]]$candidates) {
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return [IO.Path]::GetFullPath($candidate) }
    }
    $command = Get-Command "$name.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw "Не найден $name."
}

function Run([string]$tool, [string[]]$arguments, [string]$what) {
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw "Не удалось: $what." }
}

try {
    if ($Ask) {
        # The address is remembered on this computer only (artifacts is not published).
        $state = Join-Path $root 'artifacts\launcher\server.txt'
        $saved = if (Test-Path -LiteralPath $state) { (Get-Content -LiteralPath $state -Raw).Trim() } else { '' }
        $answer = Read-Host "Адрес сервера (IP или имя в Tailscale) [$saved]"
        $Server = if ($answer) { $answer.Trim() } else { $saved }
        if (-not $Server) { throw 'Адрес сервера не указан.' }
        New-Item -ItemType Directory -Force -Path (Split-Path $state) | Out-Null
        Set-Content -LiteralPath $state -Value $Server -Encoding UTF8
    }
    $node = Find-Tool 'node' @((Join-Path $root '.tools\node\node.exe'), (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'))
    $dotnet = Find-Tool 'dotnet' @((Join-Path $root '.tools\dotnet\dotnet.exe'))
    Push-Location $root
    try {
        Write-Host 'Собираем интерфейс...'
        Run $node @('node_modules\typescript\bin\tsc', '-b') 'проверка TypeScript'
        Run $node @('node_modules\vite\bin\vite.js', 'build') 'сборка интерфейса'
        Write-Host 'Собираем сервер для Linux...'
        if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
        $app = Join-Path $release 'app'
        Run $dotnet @('publish', 'server\TradeBot.Api\TradeBot.Api.csproj', '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true',
            '-o', $app, '--nologo', '-v', 'quiet') 'сборка сервера'
        Copy-Item -LiteralPath (Join-Path $root 'dist') -Destination (Join-Path $app 'wwwroot') -Recurse
        # Server files must keep Unix line endings, whatever the Windows checkout did to them.
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'scripts\server') -File) {
            if ($file.Extension -eq '.ps1' -or $file.Extension -eq '.cmd') { continue }
            $text = [IO.File]::ReadAllText($file.FullName) -replace "`r`n", "`n"
            [IO.File]::WriteAllText((Join-Path $release $file.Name), $text, $utf8)
        }
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Run (Join-Path $env:SystemRoot 'System32\tar.exe') @('-czf', $archive, '-C', $release, '.') 'упаковка'
    } finally { Pop-Location }
    $size = [math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 1)
    Write-Host "Готово: $archive ($size МБ)" -ForegroundColor Green

    if ($Server) {
        $target = "$User@$Server"
        Write-Host "Загружаем на $target..."
        Run 'scp' @('-P', "$Port", $archive, "${target}:/tmp/trade-bot-server.tar.gz") 'загрузка на сервер'
        Write-Host 'Устанавливаем...'
        $install = 'rm -rf /tmp/trade-bot-install && mkdir -p /tmp/trade-bot-install && tar -xzf /tmp/trade-bot-server.tar.gz -C /tmp/trade-bot-install && sudo bash /tmp/trade-bot-install/install.sh'
        Run 'ssh' @('-t', '-p', "$Port", $target, $install) 'установка на сервере'
        Write-Host 'Сервер обновлён.' -ForegroundColor Green
    } else {
        Write-Host 'Чтобы загрузить и установить, укажите адрес: -Server <IP или имя сервера>.'
    }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
