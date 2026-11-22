# Build two plugin packages from one source tree (issue #150).
#
#   ControlHub.Plugin-2.1.2.cipx   apiVersion 2.1.2.0   (ClassIsland 2.1.2.x, preview)
#   ControlHub.Plugin-2.1.0.cipx   apiVersion 2.1.0.1   (ClassIsland 2.1.0.x, stable)
#
# The 2.1.0 package degrades two features (clock sync / rotation offset write):
# they use host-internal types that bind a different Avalonia major version.
#
# NOTE: all text below is English on purpose. Windows PowerShell 5.1 decodes a
# BOM-less UTF-8 script as GBK, so any CJK literal here breaks quote pairing.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repoRoot 'src/IslandHub.Plugin/IslandHub.Plugin.csproj'
$cipx = Join-Path $repoRoot 'src/IslandHub.Plugin/cipx/ControlHub.Plugin.cipx'
$dist = Join-Path $repoRoot 'dist'

$env:DOTNET_CLI_HOME = Join-Path (Split-Path -Parent $repoRoot) '_dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Build-One {
    param(
        [string]$CiVersion,
        [string]$FileName,
        [string]$ApiVersion
    )

    Write-Host "==> building for ClassIsland $CiVersion (apiVersion $ApiVersion)"

    $buildArgs = @('build', $proj, '-c', 'Release', '-p:CreateCipx=true', '--nologo', '-v', 'q')
    if ($CiVersion -ne '2.1.2') {
        $buildArgs += "-p:CiVersion=$CiVersion"
    }

    $output = & dotnet $buildArgs 2>&1 | Out-String

    if ($LASTEXITCODE -ne 0 -or $output -match 'error [A-Z]+\d+') {
        Write-Host $output
        throw "build failed for $CiVersion"
    }

    if (-not (Test-Path $cipx)) {
        throw "cipx not produced for $CiVersion"
    }

    $target = Join-Path $dist $FileName

    if ($ApiVersion -eq '2.1.2.0') {
        # Default manifest already carries this value; plain copy is enough.
        Copy-Item $cipx $target -Force
    }
    else {
        # Repack with patched apiVersion: manifest.yml is a source file, not generated,
        # so it cannot vary per build configuration without post-processing the archive.
        $tmp = Join-Path $env:TEMP ('cipx-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $tmp | Out-Null

        [IO.Compression.ZipFile]::ExtractToDirectory($cipx, $tmp)

        $manifestPath = Join-Path $tmp 'manifest.yml'
        $text = [IO.File]::ReadAllText($manifestPath)
        $text = [Text.RegularExpressions.Regex]::Replace($text, '(?m)^apiVersion:\s*[\d.]+', "apiVersion: $ApiVersion")
        [IO.File]::WriteAllText($manifestPath, $text, (New-Object Text.UTF8Encoding $false))

        if (Test-Path $target) {
            Remove-Item $target -Force
        }

        [IO.Compression.ZipFile]::CreateFromDirectory($tmp, $target)
        Remove-Item $tmp -Recurse -Force
    }

    Write-Host ('    -> ' + $FileName + '  ' + [math]::Round((Get-Item $target).Length / 1KB) + ' KB')
}

Build-One -CiVersion '2.1.2' -FileName 'ControlHub.Plugin-2.1.2.cipx' -ApiVersion '2.1.2.0'
Build-One -CiVersion '2.1.0' -FileName 'ControlHub.Plugin-2.1.0.cipx' -ApiVersion '2.1.0.1'

Write-Host ''
Write-Host '==> packages ready:' -ForegroundColor Green
Get-ChildItem $dist -Filter '*.cipx' | ForEach-Object {
    Write-Host ('    ' + $_.Name + '  ' + [math]::Round($_.Length / 1KB) + ' KB')
}

exit 0
