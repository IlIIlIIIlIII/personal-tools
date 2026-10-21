param([string]$PayloadDirectory = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\Scripts\WindowsNpu.psm1') -Force
$root = Join-Path ([IO.Path]::GetTempPath()) ('PersonalTools-npu-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
function Reject([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw $Message }
}
try {
    # Isolate discovery: never depend on or modify the real machine installation.
    & (Get-Module WindowsNpu) { param($path) $script:testInstallation = $path; function script:Get-InstalledApoDirectory { $script:testInstallation } } (Join-Path $root 'installed')
    New-Item -ItemType Directory -Path (Join-Path $root 'installed') | Out-Null
    $package = Join-Path $root 'package'
    Copy-NpuPackage $package
    Set-Content (Join-Path $root 'installed\npu-required.flag') 'required'
    Reject { Copy-NpuPackage $package } 'A lost installed NPU directory silently produced a DSP package.'
    Reject { Assert-NpuPayload (Join-Path $root 'missing') } 'Missing payload accepted.'
    if ($PayloadDirectory) {
        Assert-NpuPayload $PayloadDirectory
        Copy-NpuPackage $package $PayloadDirectory
        if (!(Test-Path (Join-Path $package 'npu-required.flag'))) { throw 'Engine requirement not preserved.' }
        $target = Join-Path $package 'npu'
        Assert-NpuPayload $target
        $model = Join-Path $target 'dtln_aec_256_1.tflite'
        $saved = [IO.File]::ReadAllBytes($model)
        [IO.File]::WriteAllBytes($model, [byte[]]@(1,2,3))
        Reject { Assert-NpuPayload $target } 'Corrupted model accepted.'
        [IO.File]::WriteAllBytes($model,$saved)
        $manifest = Join-Path $target 'manifest.json'
        $original = Get-Content $manifest -Raw
        $entries = $original | ConvertFrom-Json
        @($entries | Where-Object file -ne 'openvino_c.dll') | ConvertTo-Json | Set-Content $manifest
        Reject { Assert-NpuPayload $target } 'Manifest missing a required runtime accepted.'
        $entries[0].file = '..\outside.dll'
        $entries | ConvertTo-Json | Set-Content $manifest
        Reject { Assert-NpuPayload $target } 'Manifest path traversal accepted.'
        Set-Content $manifest $original
        Assert-NpuPayload $target
        # A rebuilt manifest must not authorize replacement model weights.
        [IO.File]::WriteAllBytes($model,[byte[]]@(1,2,3))
        $entries = $original | ConvertFrom-Json
        ($entries | Where-Object file -eq 'dtln_aec_256_1.tflite').Hash = (Get-FileHash $model).Hash
        $entries | ConvertTo-Json | Set-Content $manifest
        Reject { Assert-NpuPayload $target } 'Unapproved model accepted with a matching manifest.'
    }
    Write-Output 'NPU packaging tests passed.'
} finally {
    # Only the unique directory created above; no computed external move/delete.
    if ([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
