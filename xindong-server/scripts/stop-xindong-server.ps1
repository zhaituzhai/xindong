$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverDir = Split-Path -Parent $scriptDir
$runDir = Join-Path $serverDir '.xindong'
$serverPidFile = Join-Path $runDir 'server.pid'
$panelPidFile = Join-Path $runDir 'panel.pid'

function Stop-FromPidFile([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "$Name 没有记录 PID。" -ForegroundColor Yellow
        return
    }
    $text = (Get-Content -LiteralPath $Path -Raw).Trim()
    $processId = 0
    if (-not [int]::TryParse($text, [ref]$processId)) {
        Write-Host "$Name 的 PID 文件无效。" -ForegroundColor Yellow
        return
    }
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        Write-Host "$Name 已经停止。" -ForegroundColor Gray
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        return
    }
    $stopped = $false
    try {
        & taskkill.exe /PID $processId /T /F 2>$null | Out-Null
        $process.WaitForExit(5000) | Out-Null
        $still = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $still) {
            $stopped = $true
            Write-Host "$Name 已停止，PID=$processId" -ForegroundColor Green
        } else {
            Write-Host "$Name 仍在运行，PID=$processId" -ForegroundColor Red
        }
    } catch {
        Write-Host "$Name 停止失败：$($_.Exception.Message)" -ForegroundColor Red
    }
    if ($stopped) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "保留 PID 文件，方便下次重试：$Path" -ForegroundColor Yellow
    }
}

Stop-FromPidFile $panelPidFile '管理面板'
Stop-FromPidFile $serverPidFile '后台服务'
