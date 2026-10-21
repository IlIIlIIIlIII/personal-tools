[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NpuDirectory,
    [Parameter(Mandatory)][string]$ApoPayload,
    [switch]$CheckOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsNpu.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WindowsInstall.psm1') -Force
Assert-NpuPayload $NpuDirectory
if (!(Test-Path -LiteralPath $ApoPayload -PathType Leaf)) { throw 'Missing APO payload.' }
if (!(Test-PayloadSignature (Get-AuthenticodeSignature $ApoPayload).Status.ToString() -LocalUnsigned)) { throw 'Invalid APO signature.' }
$classPath = 'HKLM:\SOFTWARE\Classes\CLSID\{4538BFC1-CCED-4C3D-A981-895A73711202}\InprocServer32'
$driver = (Get-Item $classPath).GetValue('')
$directory = [IO.Path]::GetFullPath((Split-Path -Parent $driver))
$programRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
if (!$directory.StartsWith($programRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Registered APO is outside Program Files.' }
if (!(Test-Path (Join-Path $directory 'installation-backup.clixml'))) { throw 'Missing original installation rollback metadata.' }
if ($CheckOnly) { Write-Output "NPU payload verified. Repair target: $driver. Applying restarts Windows Audio."; return }
Assert-Administrator
$stamp = (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$backup = Join-Path $directory "npu-repair-backup-$stamp"
$stage = Join-Path $directory "npu-stage-$stamp"
$target = Join-Path $directory 'npu'
$marker = Join-Path $directory 'npu-required.flag'
New-Item -ItemType Directory -Path $backup,$stage | Out-Null
Copy-Item -LiteralPath $driver -Destination (Join-Path $backup 'original-apo.dll')
Copy-Item -LiteralPath (Join-Path $directory 'installation-backup.clixml') -Destination $backup
$hadMarker = Test-Path $marker
if ($hadMarker) { Copy-Item -LiteralPath $marker -Destination $backup }
Get-ChildItem -LiteralPath $NpuDirectory -File | Copy-Item -Destination $stage
Assert-NpuPayload $stage
Copy-Item -LiteralPath $ApoPayload -Destination (Join-Path $backup 'replacement-apo.dll')
if ((Get-FileHash (Join-Path $backup 'replacement-apo.dll')).Hash -ne (Get-FileHash $ApoPayload).Hash) { throw 'Staged APO verification failed.' }
$dependents = @((Get-Service Audiosrv).DependentServices | Where-Object Status -eq Running | Select-Object -ExpandProperty Name)
$moved = $false
$published = $false
try {
    Stop-Service Audiosrv -Force
    if (Test-Path $target) {
        # Both literal paths are fixed children of the verified installation root.
        Move-Item -LiteralPath $target -Destination (Join-Path $backup 'npu')
        $moved = $true
    }
    Move-Item -LiteralPath $stage -Destination $target
    $published = $true
    Copy-Item -LiteralPath (Join-Path $backup 'replacement-apo.dll') -Destination $driver -Force
    Set-Content -LiteralPath $marker -Encoding ascii -Value 'DTLN-AEC 256; NPU required; do not silently downgrade to DSP.'
    Assert-NpuPayload $target
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WindowsNpu.psm1') -Destination $directory -Force
    Copy-Item -LiteralPath $PSCommandPath -Destination $directory -Force
    Set-Content -LiteralPath (Join-Path $directory 'npu-repair-status.txt') -Value "Installed $stamp. Backup: $backup. Live NPU callbacks still require verification."
} catch {
    $failure = $_
    Copy-Item -LiteralPath (Join-Path $backup 'original-apo.dll') -Destination $driver -Force
    if ($published) { Move-Item -LiteralPath $target -Destination (Join-Path $backup 'failed-npu') }
    if ($moved) { Move-Item -LiteralPath (Join-Path $backup 'npu') -Destination $target }
    if ($hadMarker) { Copy-Item -LiteralPath (Join-Path $backup 'npu-required.flag') -Destination $marker -Force }
    elseif (Test-Path $marker) { Remove-Item -LiteralPath $marker }
    throw "NPU repair failed; previous installation restored. $failure"
} finally {
    Start-Service Audiosrv
    foreach ($name in $dependents) { Start-Service -Name $name }
}
Write-Output "NPU restored in $directory. Backup: $backup. Verify neuralState=2 and increasing neuralBlocks on a reopened microphone stream."
