Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# CMake launched from PowerShell 7 can pass its module path to Windows PowerShell.
# Load this host's utility module explicitly (Get-FileHash / JSON cmdlets).
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')

function Assert-NpuPayload([string]$Directory) {
    $required = @('enabled.flag','manifest.json','openvino.dll','openvino_c.dll',
        'openvino_intel_npu_plugin.dll','openvino_tensorflow_lite_frontend.dll',
        'dtln_aec_256_1.tflite','dtln_aec_256_2.tflite','LICENSE-DTLN-AEC.txt','LICENSE-OpenVINO.txt')
    foreach ($name in $required) {
        if (!(Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) { throw "NPU payload missing: $name ($Directory)" }
    }
    $entries = Get-Content -LiteralPath (Join-Path $Directory 'manifest.json') -Raw | ConvertFrom-Json
    $seen = @{}
    foreach ($entry in $entries) {
        $name = [string]$entry.file
        if (!$name -or $name -match '[/\\:]' -or $name -in @('.','..') -or $seen.ContainsKey($name)) { throw "Invalid NPU manifest entry: $name" }
        $seen[$name] = $true
        $file = Join-Path $Directory $name
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Hash) { throw "NPU payload hash mismatch: $name" }
    }
    foreach ($name in $required | Where-Object { $_ -notin @('enabled.flag','manifest.json') }) {
        if (!$seen.ContainsKey($name)) { throw "NPU manifest omits required file: $name" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File) {
        if ($file.Name -notin @('enabled.flag','manifest.json') -and !$seen.ContainsKey($file.Name)) { throw "Unverified NPU payload file: $($file.Name)" }
    }
    $models = @{
        'dtln_aec_256_1.tflite' = '4A3A588B69FD79D837BC068B579A26FAA92CAC39DDDBB00001D2DC1C3D869D60'
        'dtln_aec_256_2.tflite' = 'FA2590243AAD1BF893C5BE45B20709E8C50FEEC65E3604D1D52BAE6EEDDC23D3'
    }
    foreach ($name in $models.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash -ne $models[$name]) { throw "Unexpected DTLN-AEC model: $name" }
    }
}

function Get-InstalledApoDirectory {
    $key = Get-Item 'HKLM:\SOFTWARE\Classes\CLSID\{4538BFC1-CCED-4C3D-A981-895A73711202}\InprocServer32' -ErrorAction SilentlyContinue
    if ($key) { try { return Split-Path -Parent $key.GetValue('') } finally { $key.Close() } }
}

function Copy-NpuPackage([string]$PackageDirectory, [string]$NpuDirectory = '') {
    # Follow the registered driver, including legacy SoundControl installations.
    $installed = Get-InstalledApoDirectory
    if (!$NpuDirectory -and $installed -and ((Test-Path (Join-Path $installed 'npu-required.flag')) -or (Test-Path (Join-Path $installed 'npu\enabled.flag')))) {
        $NpuDirectory = Join-Path $installed 'npu'
    }
    $target = Join-Path $PackageDirectory 'npu'
    if (!$NpuDirectory -and (Test-Path $target)) { $NpuDirectory = $target }
    if (!$NpuDirectory) {
        if (Test-Path (Join-Path $PackageDirectory 'npu-required.flag')) { throw 'NPU was enabled for this package but its payload is missing.' }
        return
    }
    Assert-NpuPayload $NpuDirectory
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    if ([IO.Path]::GetFullPath($target) -ne [IO.Path]::GetFullPath($NpuDirectory)) {
        Get-ChildItem -LiteralPath $NpuDirectory -File | Copy-Item -Destination $target -Force
    }
    Assert-NpuPayload $target
    Set-Content -LiteralPath (Join-Path $PackageDirectory 'npu-required.flag') -Encoding ascii -Value 'DTLN-AEC 256; NPU required; do not silently downgrade to DSP.'
}
Export-ModuleMember -Function Assert-NpuPayload,Get-InstalledApoDirectory,Copy-NpuPackage
