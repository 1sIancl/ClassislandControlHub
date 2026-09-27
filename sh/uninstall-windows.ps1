#Requires -Version 5.1
<#
.SYNOPSIS
    ClassislandControlHub 集控 A 端服务器 · Windows 卸载脚本。

.DESCRIPTION
    停止并删除 Windows 服务、清理防火墙规则。默认保留数据目录（课表/设备/注册码），
    如需连同数据一并删除，请加 -RemoveData。

    必须在“以管理员身份运行”的 PowerShell 中执行。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\sh\uninstall-windows.ps1

.EXAMPLE
    # 同时删除数据目录
    powershell -ExecutionPolicy Bypass -File .\sh\uninstall-windows.ps1 -RemoveData
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'ClassislandControlHub',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ClassislandControlHub'),
    [string]$DataDir = (Join-Path $env:ProgramData 'ClassislandControlHub\data'),
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$Message) { Write-Host "[集控] $Message" -ForegroundColor Cyan }
function Write-Die([string]$Message) { Write-Host "[错误] $Message" -ForegroundColor Red; exit 1 }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Die '请右键点击 PowerShell 选择“以管理员身份运行”后重试。'
}

# ── 停止并删除服务 ────────────────────────────────────────
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Info "停止服务 $ServiceName …"
    & sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Info "删除服务 $ServiceName …"
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
else {
    Write-Info "未找到服务 $ServiceName，跳过。"
}

# ── 清理防火墙规则 ────────────────────────────────────────
if (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue) {
    $rules = Get-NetFirewallRule -DisplayName 'ClassislandControlHub*' -ErrorAction SilentlyContinue
    if ($rules) {
        Write-Info '删除防火墙规则…'
        $rules | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    }
}

# ── 删除程序目录 ──────────────────────────────────────────
if (Test-Path $InstallDir) {
    Write-Info "删除程序目录 $InstallDir …"
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
}

# ── 可选：删除数据目录 ────────────────────────────────────
if ($RemoveData) {
    if (Test-Path $DataDir) {
        Write-Info "删除数据目录 $DataDir …"
        Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
    }
}
else {
    Write-Info "已保留数据目录：$DataDir（如需删除请加 -RemoveData）"
}

Write-Info '卸载完成。'
