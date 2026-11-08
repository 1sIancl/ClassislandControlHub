#Requires -Version 5.1
<#
.SYNOPSIS
    Build the server and prove the output is actually fresh.

.DESCRIPTION
    Why this script exists -- a real trap hit on this machine:

    On some machines `dotnet` CLI fails during *startup*, before it ever
    reaches the compiler:

        System.IO.IOException: <path contains an untrusted mount point>
          C:\Users\<user>\.dotnet\10.0.401.toolpath.sentinel

    It still prints a stack trace and the process may exit 0. Filter that
    output with `Select-String 'error'` and you see "no errors" -- while
    nothing at all was compiled. That cost several rounds of false
    "build passed" before comparing dll timestamps revealed it.

    So this script does two things:
      1. Points DOTNET_CLI_HOME inside the workspace to avoid that path.
      2. Verifies the dll timestamp afterwards -- never trust the exit code alone.

.PARAMETER Project
    Project to build. Defaults to the server.

.PARAMETER Configuration
    Defaults to Release.

.EXAMPLE
    powershell -File sh/build.ps1
    powershell -File sh/build.ps1 -Project src/IslandHub.Plugin/IslandHub.Plugin.csproj
#>
param(
    [string]$Project = 'src/IslandHub.Server/IslandHub.Server.csproj',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cliHome = Join-Path (Split-Path -Parent $repoRoot) '_dotnet-home'

# (1) Redirect the CLI home into the workspace.
New-Item -ItemType Directory -Force -Path $cliHome | Out-Null
$env:DOTNET_CLI_HOME = $cliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$startedAt = Get-Date

Write-Host "==> building $Project ($Configuration)" -ForegroundColor Cyan
$output = & dotnet build $Project -c $Configuration --nologo 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { $_ }

if ($exitCode -ne 0) {
    Write-Host "==> build failed (exit $exitCode)" -ForegroundColor Red
    exit $exitCode
}

# CLI startup failure can exit 0 -- catch its signature explicitly.
$cliBroken = $output | Select-String -Pattern 'toolpath.sentinel|untrusted mount'
if ($cliBroken) {
    Write-Host "==> dotnet CLI itself failed to start (not a code problem). Check DOTNET_CLI_HOME." -ForegroundColor Red
    exit 3
}

# (2) Verify the artifact is new.
$dll = Get-ChildItem -Path (Join-Path $repoRoot 'src/IslandHub.Server/bin') -Recurse -Filter 'IslandHub.Server.dll' `
    -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match [regex]::Escape($Configuration) } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $dll) {
    Write-Host "==> artifact not found" -ForegroundColor Red
    exit 4
}

$stamp = $dll.LastWriteTime.ToString('HH:mm:ss')

# "dll not refreshed" is only a problem when a source file is NEWER than it --
# otherwise it just means incremental build decided nothing changed, which is fine.
$newestSource = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Include '*.cs', '*.csproj' `
    -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($newestSource -and $newestSource.LastWriteTime -gt $dll.LastWriteTime.AddSeconds(1)) {
    Write-Host "==> STALE: $($newestSource.Name) is newer than the artifact (stamp $stamp)" -ForegroundColor Red
    exit 5
}

if ($dll.LastWriteTime -lt $startedAt.AddSeconds(-5)) {
    Write-Host "==> done (no source change, artifact stamp $stamp)" -ForegroundColor Green
    exit 0
}

Write-Host "==> done, artifact stamp $stamp" -ForegroundColor Green
exit 0
