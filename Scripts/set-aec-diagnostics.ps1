[CmdletBinding()]
param([ValidateSet('Enable','Disable','Status')][string]$Action='Status')
$ErrorActionPreference='Stop'
$data=Join-Path $env:ProgramData 'PersonalTools'
if (!(Test-Path (Join-Path $data 'state-v1.bin')) -and (Test-Path "$env:ProgramData\SoundControl\state-v1.bin")) { $data="$env:ProgramData\SoundControl" }
$root=Join-Path $data 'aec-diagnostics'
$flag=Join-Path $root 'enabled.flag'
if ($Action -eq 'Enable') {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Set-Content -LiteralPath $flag -Encoding ascii -Value 'User-authorized mic / speaker / output recording. 14 GB log cap. Reopen microphone to begin.'
}
if ($Action -eq 'Disable' -and (Test-Path -LiteralPath $flag)) { Remove-Item -LiteralPath $flag }
$files=@(Get-ChildItem -LiteralPath $root -Filter '*.acd' -ErrorAction SilentlyContinue)
$bytes=($files | Measure-Object Length -Sum).Sum
[pscustomobject]@{Directory=$root;Enabled=(Test-Path -LiteralPath $flag);ReservedBytes=$bytes;Segments=$files.Count;LatestWrite=($files | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty LastWriteTime);Note='Enable applies to newly opened microphone streams; Disable stops recording within about 1 second. Existing files are retained.'}
