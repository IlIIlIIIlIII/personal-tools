param([Parameter(Mandatory)][string]$PackageDirectory, [string]$NpuDirectory = '')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsNpu.psm1') -Force
Copy-NpuPackage $PackageDirectory $NpuDirectory
