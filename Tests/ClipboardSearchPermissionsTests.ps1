$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('ClipboardPermissionChecks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script = Join-Path $PSScriptRoot '..\Scripts\configure-clipboard-search.ps1'
$rules = @(& $script -ArchivePath $root -Preview)
$patterns = @($rules | Where-Object { $_.StartsWith('command(regex:') } | ForEach-Object { $_.Substring(14, $_.Length - 15) })
function Assert-Match([string]$Command, [bool]$Expected) {
    $actual = @($patterns | Where-Object { $Command -match $_ }).Count -gt 0
    if ($actual -ne $Expected) { throw "Permission match did not equal $Expected" }
}
$list = "Get-ChildItem -LiteralPath '$root' -Recurse -File -Filter 'metadata.json' | Select-Object -ExpandProperty FullName"
$search = "Get-ChildItem -LiteralPath '$root' -Recurse -File -Filter '*.txt' | Select-String -SimpleMatch -List -Pattern '한글 검색' | Select-Object -ExpandProperty Path"
Assert-Match $list $true
Assert-Match $search $true
Assert-Match ($search.Replace('Select-Object -ExpandProperty Path', 'Select-Object Path,Line | ConvertTo-Json -Compress')) $true
Assert-Match ($search + '; Remove-Item anything') $false
Assert-Match ($search.Replace($root, 'C:\Users')) $false
Assert-Match ($search.Replace("'한글 검색'", "'x'; whoami; '")) $false
Assert-Match ($search.Replace('-File', '-File -FollowSymlink')) $false
Assert-Match ($search.Replace('*.txt', '*')) $false
Write-Output '8 clipboard search permission checks passed; CLI settings were not changed.'
