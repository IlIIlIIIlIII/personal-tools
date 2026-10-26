# Updates an installed Personal Tools in place from a newer build package:
# APO, NPU host and payload, setup helper, scripts and the app. Endpoint
# registrations, filters, settings and the installation backup are kept.
# Windows Audio is restarted so audiodg loads the new APO; playback and
# recording are interrupted for a few seconds. Close Personal Tools first.
[CmdletBinding()]
param([string]$SourceDirectory = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) { $SourceDirectory = $PSScriptRoot }
Import-Module (Join-Path $PSScriptRoot 'WindowsInstall.psm1') -Force
Assert-Administrator
$destination = Join-Path $env:ProgramFiles 'PersonalTools'
$backupPath = Join-Path $destination 'installation-backup.clixml'
if (-not (Test-Path -LiteralPath $backupPath)) { throw 'Personal Tools audio effects are not installed. Use install-windows.ps1.' }
$backup = Import-Clixml -LiteralPath $backupPath
$log = Join-Path $destination 'update.log'
function Write-Log([string]$text) { Add-Content -LiteralPath $log -Encoding utf8 -Value ("{0:s} {1}" -f (Get-Date), $text); Write-Output $text }

$files = @('PersonalToolsAPO.dll','PersonalToolsSetup.exe','install-windows.ps1','uninstall-windows.ps1','update-windows.ps1','WindowsInstall.psm1',
    'WindowsDeviceRecovery.psm1','repair-windows-device.ps1','repair-windows-capture.ps1','repair-windows-microphone.ps1','register-windows-device-recovery.ps1')
$folders = @('ui')
$signed = @('PersonalToolsAPO.dll','PersonalToolsSetup.exe','ui\PersonalToolsBridge.dll','ui\PersonalToolsWindows.exe','ui\PersonalToolsWindows.dll')
if (Test-Path -LiteralPath (Join-Path $SourceDirectory 'npu\enabled.flag')) {
    Import-Module (Join-Path $PSScriptRoot 'WindowsNpu.psm1') -Force -ErrorAction SilentlyContinue
    $files += 'PersonalToolsNpuHost.exe'; $folders += 'npu'; $signed += 'PersonalToolsNpuHost.exe'
}
if (Test-Path -LiteralPath (Join-Path $SourceDirectory 'EQ')) { $folders += 'EQ' }
foreach ($name in $files + $folders) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $name))) { throw "Missing payload: $name" }
}
foreach ($name in $signed) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $SourceDirectory $name)
    if (-not (Test-PayloadSignature $signature.Status.ToString() -LocalUnsigned:([bool]$backup.LocalUnsigned))) {
        throw "Update blocked: $name signature is $($signature.Status). Nothing was changed."
    }
}
$running = @(Get-Process PersonalToolsWindows -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($destination, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) { throw 'Close Personal Tools (앱 종료) before updating. Nothing was changed.' }
Get-Process PersonalToolsNpuHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($destination, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force

Write-Log "Update from $SourceDirectory"
$audio = Get-Service Audiosrv
$dependents = @($audio.DependentServices | Where-Object Status -eq 'Running')
Stop-Service -InputObject $audio -Force
try {
    foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $SourceDirectory $name) -Destination (Join-Path $destination $name) -Force }
    foreach ($name in $folders) {
        $target = Join-Path $destination $name
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $SourceDirectory $name) | Copy-Item -Destination $target -Recurse -Force
    }
    if ($folders -contains 'npu' -and (Get-Command Assert-NpuPayload -ErrorAction SilentlyContinue)) { Assert-NpuPayload (Join-Path $destination 'npu') }
    Write-Log 'Files updated'
}
finally {
    Start-Service -InputObject $audio
    foreach ($service in $dependents) { Start-Service -InputObject $service -ErrorAction SilentlyContinue }
    Write-Log "Windows Audio restarted: $((Get-Service Audiosrv).Status)"
}
# Picks up recovery tasks added since the installation (microphone recovery, 2026-10-25).
& (Join-Path $destination 'register-windows-device-recovery.ps1') | ForEach-Object { Write-Log $_ }
Write-Output 'Updated. Reopen Personal Tools and any app that was recording or playing.'
