$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:29800/api/v1'
$data = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'demo-data.json')) | ConvertFrom-Json

function J($o) { , [Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Compress -Depth 8)) }

function Send($method, $path, $obj) {
  try {
    return Invoke-RestMethod -Uri "$base$path" -Method $method -Headers $script:h `
      -ContentType 'application/json' -Body (J $obj)
  } catch {
    $resp = $_.Exception.Response
    if ($resp) {
      $reader = New-Object IO.StreamReader($resp.GetResponseStream())
      Write-Output "HTTP_ERR $method $path -> $([int]$resp.StatusCode) $($reader.ReadToEnd())"
    } else {
      Write-Output "ERR $method $path -> $($_.Exception.Message)"
    }
    throw
  }
}

$login = Invoke-RestMethod -Uri "$base/admin/login" -Method Post -ContentType 'application/json' `
  -Body ([Text.Encoding]::UTF8.GetBytes('{"username":"admin","password":"admin123"}'))
$script:h = @{ Authorization = "Bearer $($login.data.token)" }

# 1) devices
$devices = (Invoke-RestMethod -Uri "$base/admin/devices" -Headers $h).data
Write-Output "DEVICES=$($devices.Count)"

# 2) buildings and floors (idempotent: reuse by name)
$existing = (Invoke-RestMethod -Uri "$base/admin/groups" -Headers $h).data
$byName = @{}
foreach ($g in $existing) { $byName[$g.name] = $g.id }
$floorId = @{}
$floorName = @{}
foreach ($b in $data.buildings) {
  if ($byName.ContainsKey($b.name)) {
    $buildingId = $byName[$b.name]
  } else {
    $buildingId = (Send 'Post' '/admin/groups' @{ name = $b.name; kind = 'building' }).data.id
  }
  Write-Output "BUILDING=$($b.name)"
  foreach ($f in $b.floors) {
    if ($byName.ContainsKey($f.name)) {
      $fid = $byName[$f.name]
    } else {
      $fid = (Send 'Post' '/admin/groups' @{ name = $f.name; parentId = $buildingId }).data.id
    }
    $floorId[$f.key] = $fid
    $floorName[$f.key] = $f.name
    Write-Output "FLOOR=$($f.name)"
  }
}

# 3) sample profile (reuse if already created)
$profiles = (Invoke-RestMethod -Uri "$base/admin/profiles" -Headers $h).data
$sample = $profiles | Where-Object { $_.name -like $data.profileNamePattern } | Select-Object -First 1
if (-not $sample) {
  $sample = (Send 'Post' '/admin/profiles/sample' @{}).data.profile
}
Write-Output "PROFILE=$($sample.name) id=$($sample.id)"

# 4) rename simulated devices via CSV import (matched by device id)
$sorted = $devices | Sort-Object name
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add($data.csvHeader)
for ($i = 0; $i -lt $data.classes.Count -and $i -lt $sorted.Count; $i++) {
  $c = $data.classes[$i]
  $lines.Add("$($c.name),$($sorted[$i].id),$($floorName[$c.floor]),$($sample.name),$($c.remark)")
}
$csv = ($lines -join "`r`n")
$import = Send 'Post' '/admin/devices/import' @{ csv = $csv; dryRun = $false }
Write-Output "IMPORT created=$($import.data.created) updated=$($import.data.updated) failed=$($import.data.failed)"

# 5) bind default profile to floors
foreach ($key in $floorId.Keys) {
  Send 'Put' "/admin/groups/$($floorId[$key])" @{ name = $floorName[$key]; defaultProfileId = $sample.id } | Out-Null
}
Write-Output 'GROUP_PROFILE_BOUND=done'

# 6) summary
$after = (Invoke-RestMethod -Uri "$base/admin/devices" -Headers $h).data
$online = ($after | Where-Object { $_.online }).Count
Write-Output "FINAL total=$($after.Count) online=$online"
