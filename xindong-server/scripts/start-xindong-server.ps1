param(
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverDir = Split-Path -Parent $scriptDir
$runDir = Join-Path $serverDir '.xindong'
$serverPidFile = Join-Path $runDir 'server.pid'
$panelPidFile = Join-Path $runDir 'panel.pid'
$serverLog = Join-Path $runDir 'logs\server.log'

function Write-Step([string]$Text) {
    Write-Host "[XinDong] $Text" -ForegroundColor Cyan
}

function Test-Port([int]$Port) {
    $client = $null
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        if (-not $task.Wait(800)) { return $false }
        return $client.Connected
    } catch {
        return $false
    } finally {
        if ($null -ne $client) { $client.Close() }
    }
}

function Find-Python {
    $candidates = @(
        (Join-Path $serverDir '..\..\XinDong-Server-Env\Scripts\python.exe'),
        (Join-Path $serverDir '.venv\Scripts\python.exe'),
        (Join-Path $serverDir 'venv\Scripts\python.exe')
    )
    foreach ($candidate in $candidates) {
        $full = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath $full) { return $full }
    }

    $python = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($null -ne $python) { return $python.Source }
    $py = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($null -ne $py) {
        $venv = Join-Path $serverDir '.venv'
        if (-not (Test-Path -LiteralPath $venv)) {
            Write-Step '没有找到 Python，正在用 py 创建 .venv…'
            & $py.Source -3 -m venv $venv
        }
        $created = Join-Path $venv 'Scripts\python.exe'
        if (Test-Path -LiteralPath $created) { return $created }
    }
    return $null
}

function Get-PanelProcess {
    if (-not (Test-Path -LiteralPath $panelPidFile)) { return $null }
    $idText = (Get-Content -LiteralPath $panelPidFile -Raw).Trim()
    $panelId = 0
    if (-not [int]::TryParse($idText, [ref]$panelId)) { return $null }
    return Get-Process -Id $panelId -ErrorAction SilentlyContinue
}

function Start-Panel([string]$Pythonw) {
    $existing = Get-PanelProcess
    if ($null -ne $existing) {
        Write-Step "管理面板已经在运行，PID=$($existing.Id)"
        return
    }

    $panelArgs = @('panel\app.py')
    $panel = Start-Process -FilePath $Pythonw -ArgumentList $panelArgs `
        -WorkingDirectory $serverDir -PassThru
    $panel.Id | Set-Content -LiteralPath $panelPidFile -Encoding ascii
    Write-Step "管理面板已启动，PID=$($panel.Id)"
}

Write-Step "服务端目录：$serverDir"

if (Test-Port 8770) {
    Write-Host ''
    Write-Host '端口 8770 已经被占用，可能已经有一个薪动服务器在运行。' -ForegroundColor Yellow
    Write-Host '如果占用者是当前正在使用的服务器，请先退出它，再运行本脚本。' -ForegroundColor Yellow
    Write-Host '如果只是想打开当前服务器文档：http://127.0.0.1:8770/docs' -ForegroundColor Yellow
    if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:8770/docs' }
    exit 1
}

$python = Find-Python
if ($null -eq $python -or -not (Test-Path -LiteralPath $python)) {
    Write-Host ''
    Write-Host '没有找到 Python 3.11+。' -ForegroundColor Red
    Write-Host '请先安装 Python 3.11，或把 Python 装到：' -ForegroundColor Red
    Write-Host 'D:\Software_install\XinDong-Server-Env' -ForegroundColor Red
    exit 1
}

$pythonDir = Split-Path -Parent $python
$pythonw = Join-Path $pythonDir 'pythonw.exe'
if (-not (Test-Path -LiteralPath $pythonw)) { $pythonw = $python }
Write-Step "使用 Python：$python"

Write-Step '检查依赖…'
& $python -c "import fastapi, uvicorn, yaml, apscheduler, httpx, flet" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Step '依赖不完整，正在安装 requirements.txt…'
    & $python -m pip install -r (Join-Path $serverDir 'requirements.txt')
    if ($LASTEXITCODE -ne 0) {
        Write-Host '依赖安装失败，请查看上面的 pip 输出。' -ForegroundColor Red
        exit 1
    }
}

New-Item -ItemType Directory -Path $runDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runDir 'logs') -Force | Out-Null

Write-Step '后台启动 FastAPI 服务…'
$serverOutLog = Join-Path $runDir 'logs\launcher.out.log'
$serverErrLog = Join-Path $runDir 'logs\launcher.err.log'
$server = Start-Process -FilePath $python `
    -ArgumentList @('-m', 'app.main') `
    -WorkingDirectory $serverDir `
    -WindowStyle Hidden `
    -RedirectStandardOutput $serverOutLog `
    -RedirectStandardError $serverErrLog `
    -PassThru
$server.Id | Set-Content -LiteralPath $serverPidFile -Encoding ascii
Write-Step "服务进程 PID=$($server.Id)，等待 /health…"

$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    if ($server.HasExited) { break }
    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8770/health' `
            -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch {
    }
}

if (-not $ready) {
    Write-Host ''
    Write-Host '服务启动失败，最后日志：' -ForegroundColor Red
    if (Test-Path -LiteralPath $serverErrLog) {
        Get-Content -LiteralPath $serverErrLog -Tail 30
    }
    if (Test-Path -LiteralPath $serverLog) {
        Get-Content -LiteralPath $serverLog -Tail 30
    }
    if (-not (Test-Path -LiteralPath $serverErrLog) -and -not (Test-Path -LiteralPath $serverLog)) {
        Write-Host "没有找到日志：$serverLog" -ForegroundColor Red
    }
    if (-not $server.HasExited) {
        try { & taskkill.exe /PID $server.Id /T /F 2>$null | Out-Null } catch { }
    }
    exit 1
}

Write-Step '服务已就绪：http://127.0.0.1:8770'
Write-Step 'Swagger：http://127.0.0.1:8770/docs'
$lanIp = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
    Where-Object { $_.IPv4DefaultGateway -and $_.IPv4Address } |
    ForEach-Object { $_.IPv4Address.IPAddress } |
    Select-Object -First 1
if ($lanIp) {
    Write-Step "局域网客户端地址：http://${lanIp}:8770"
    Write-Step '朋友电脑必须和这台电脑在同一局域网，防火墙需放行 TCP 8770。'
}

Start-Panel $pythonw

if (-not $NoBrowser) {
    Start-Process 'http://127.0.0.1:8770/docs'
}

Write-Host ''
Write-Host '一键启动完成。' -ForegroundColor Green
Write-Host '关闭这个窗口不会停止后台服务。' -ForegroundColor Green
Write-Host '停止后台：双击 stop-xindong-server.bat' -ForegroundColor Green
