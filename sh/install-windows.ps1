#Requires -Version 5.1
<#
.SYNOPSIS
    ClassislandControlHub 集控 A 端服务器 · Windows 一键部署脚本。

.DESCRIPTION
    发布服务端程序并注册为 Windows 服务，实现开机自启、崩溃自动重启；
    同时配置防火墙放行 HTTP 端口与局域网自动发现（UDP）端口。

    数据默认保存在 %ProgramData%\ClassislandControlHub\data，与服务程序分离，
    重新部署/升级不会影响已配置的课表、设备与注册码。

    必须在“以管理员身份运行”的 PowerShell 中执行。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\sh\install-windows.ps1

.EXAMPLE
    # 自定义端口与安装目录
    powershell -ExecutionPolicy Bypass -File .\sh\install-windows.ps1 -HttpPort 8080 -InstallDir D:\ControlHub
#>
[CmdletBinding()]
param(
    # 服务端程序安装目录。
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ClassislandControlHub'),

    # 数据目录（SQLite 与自动备份所在）。
    [string]$DataDir = (Join-Path $env:ProgramData 'ClassislandControlHub\data'),

    # HTTP 监听端口。
    [int]$HttpPort = 29800,

    # 局域网自动发现（UDP）端口。
    [int]$DiscoveryPort = 29810,

    # Windows 服务名称。
    [string]$ServiceName = 'ClassislandControlHub',

    # 跳过防火墙规则配置。
    [switch]$NoFirewall
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$Message) { Write-Host "[集控] $Message" -ForegroundColor Cyan }
function Write-Warn2([string]$Message) { Write-Host "[警告] $Message" -ForegroundColor Yellow }
function Write-Die([string]$Message) { Write-Host "[错误] $Message" -ForegroundColor Red; exit 1 }

# ── 权限检查 ──────────────────────────────────────────────
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Die '请右键点击 PowerShell 选择“以管理员身份运行”后重试。'
}

# ── 定位源码仓库 ──────────────────────────────────────────
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $repoRoot 'src\ControlHub.Server\ControlHub.Server.csproj'
if (-not (Test-Path $serverProject)) {
    Write-Die "未找到服务端项目：$serverProject。请在本仓库的 sh 目录中运行本脚本。"
}

# ── 定位 .NET 10 SDK ──────────────────────────────────────
function Resolve-DotNet {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')

    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -match '^10\.') { return $candidate }
    }
    return $null
}

$dotnet = Resolve-DotNet
if (-not $dotnet) {
    Write-Info '未检测到 .NET 10 SDK，尝试自动安装到 C:\Program Files\dotnet …'
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    }
    catch {
        Write-Die "下载 .NET 安装脚本失败：$($_.Exception.Message)。请手动安装 .NET 10 SDK 后重试。"
    }
    & $installer -Channel '10.0' -InstallDir (Join-Path $env:ProgramFiles 'dotnet')
    $dotnet = Resolve-DotNet
    if (-not $dotnet) {
        Write-Die '自动安装 .NET 10 SDK 失败，请手动安装后重试：https://dotnet.microsoft.com/download/dotnet/10.0'
    }
}
Write-Info "使用 .NET SDK：$dotnet"

# ── 发布服务端 ────────────────────────────────────────────
$serverDir = Join-Path $InstallDir 'server'
Write-Info "发布服务端到 $serverDir …"
& $dotnet publish $serverProject -c Release -o $serverDir --nologo
if ($LASTEXITCODE -ne 0) { Write-Die '编译发布失败，请检查上面的错误信息。' }

# ── 写入部署配置 ──────────────────────────────────────────
$appSettings = Join-Path $serverDir 'appsettings.json'
if (-not (Test-Path $appSettings)) { Write-Die "发布目录缺少 appsettings.json：$appSettings" }

Write-Info "写入配置（HTTP $HttpPort，数据目录 $DataDir）…"
$json = Get-Content $appSettings -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $json.PSObject.Properties['ControlHub']) {
    $json | Add-Member -NotePropertyName ControlHub -NotePropertyValue ([pscustomobject]@{})
}
$json.ControlHub.HttpPort = $HttpPort
$json.ControlHub.DiscoveryPort = $DiscoveryPort
$json.ControlHub.DataDirectory = $DataDir
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
[IO.File]::WriteAllText($appSettings, ($json | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))

# ── 注册 Windows 服务 ─────────────────────────────────────
$exePath = Join-Path $serverDir 'ControlHub.Server.exe'
if (-not (Test-Path $exePath)) { Write-Die "发布目录缺少可执行文件：$exePath" }

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Info "服务 $ServiceName 已存在，先停止并删除以便重新部署…"
    & sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $ServiceName | Out-Null

    # 服务删除是异步的，需等 SCM 真正移除后再创建，否则会报“已标记为删除”。
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Die "旧服务 $ServiceName 删除超时，请重启后重试。"
    }
}

Write-Info "注册服务 $ServiceName …"
# sc.exe 需要 `key= value` 形式（`=` 后必须有空格），值由 PowerShell 自动加引号。
& sc.exe create $ServiceName 'binPath=' $exePath 'start=' 'auto' 'DisplayName=' 'ClassislandControlHub 集控服务器' | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Die '创建服务失败。' }
& sc.exe description $ServiceName 'ClassislandControlHub 集控服务器：统一下发课表/时间表/科目，管理与同步教室大屏。' | Out-Null
# 崩溃后自动重启（首次 5 秒，之后每 5 秒，计数器每天重置）。
& sc.exe failure $ServiceName 'reset=' '86400' 'actions=' 'restart/5000/restart/5000/restart/5000' | Out-Null

# ── 防火墙放行 ────────────────────────────────────────────
if (-not $NoFirewall -and (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue)) {
    Write-Info '配置防火墙规则…'
    Get-NetFirewallRule -DisplayName "ClassislandControlHub*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "ClassislandControlHub HTTP ($HttpPort)" -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $HttpPort | Out-Null
    New-NetFirewallRule -DisplayName "ClassislandControlHub 自动发现 ($DiscoveryPort)" -Direction Inbound -Action Allow `
        -Protocol UDP -LocalPort $DiscoveryPort | Out-Null
}
elseif (-not $NoFirewall) {
    Write-Warn2 '未找到 New-NetFirewallRule，已跳过防火墙配置，请手动放行端口。'
}

# ── 启动并自检 ────────────────────────────────────────────
Write-Info '启动服务…'
& sc.exe start $ServiceName | Out-Null
Start-Sleep -Seconds 3

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -eq 'Running') {
    try {
        $ping = Invoke-WebRequest -Uri "http://127.0.0.1:$HttpPort/api/v1/ping" -UseBasicParsing -TimeoutSec 10
        Write-Info "HTTP 自检通过：http://127.0.0.1:$HttpPort"
    }
    catch {
        Write-Warn2 "服务已启动，但 HTTP 自检未通过（可能仍在初始化）。请稍后重试。"
    }
}
else {
    Write-Warn2 "服务未能启动。请查看事件查看器或运行：& '$env:SystemRoot\System32\sc.exe' query $ServiceName"
}

Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host '  部署完成！' -ForegroundColor Green
Write-Host ''
Write-Host "  本机访问：  http://127.0.0.1:$HttpPort"
Write-Host "  局域网访问：http://<本机IP>:$HttpPort"
Write-Host ''
Write-Host '  默认账号：admin   默认密码：admin123（登录后请立即修改）'
Write-Host ''
Write-Host "  服务名：$ServiceName"
Write-Host "  程序目录：$serverDir"
Write-Host "  数据目录：$DataDir"
Write-Host ''
Write-Host '  常用命令：'
Write-Host "    查看状态   Get-Service $ServiceName"
Write-Host "    重启服务   Restart-Service $ServiceName"
Write-Host "    停止服务   Stop-Service $ServiceName"
Write-Host "    卸载       powershell -ExecutionPolicy Bypass -File .\sh\uninstall-windows.ps1"
Write-Host '============================================================' -ForegroundColor Green
