[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Get-AppxPackage -Name 'PersonalTools.HdrWidget' | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
Write-Output 'Removed the HDR widget for the current user. Personal Tools and HDR program settings are preserved.'
