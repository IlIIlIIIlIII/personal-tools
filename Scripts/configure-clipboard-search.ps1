param([Parameter(Mandatory=$true)][string]$ArchivePath, [switch]$Preview)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ArchivePath).TrimEnd('\')
if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Archive directory does not exist.' }
if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Archive root must not be a link.' }
$literalRoot = $root.Replace("'", "''")
$prefix = [regex]::Escape("Get-ChildItem -LiteralPath '$literalRoot' -Recurse -File -Filter '")
$glob = '(metadata\.json|entry\.md|\*\.txt)'
$listRule = '^' + $prefix + $glob + [regex]::Escape("' | Select-Object -ExpandProperty FullName") + '$'
$searchRule = '^' + $prefix + $glob + [regex]::Escape("' | Select-String -SimpleMatch -List -Pattern '") + "[^'\r\n]+" + [regex]::Escape("' | Select-Object -ExpandProperty Path") + '$'
$linesRule = '^' + $prefix + $glob + [regex]::Escape("' | Select-String -SimpleMatch -List -Pattern '") + "[^'\r\n]+" + [regex]::Escape("' | Select-Object Path,Line | ConvertTo-Json -Compress") + '$'
$grants = @("read_file($($root.Replace('\','/')))", "command(regex:$listRule)", "command(regex:$searchRule)", "command(regex:$linesRule)")
if ($Preview) { $grants; return }
# Rules match the complete read-only pipeline and a fixed absolute directory.
# No arbitrary shell prefixes, extra switches, redirections or trailing commands.
$settingsPath = Join-Path $env:USERPROFILE '.gemini\antigravity-cli\settings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
if (!$settings.permissions) { $settings | Add-Member permissions ([pscustomobject]@{}) }
$existing = @($settings.permissions.allow | Where-Object { $_ })
$merged = @($existing + $grants | Select-Object -Unique)
if ($merged.Count -eq $existing.Count) { Write-Output 'Archive search rules already present.'; return }
$backup = $settingsPath + '.backup-clipboard-search-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
Copy-Item -LiteralPath $settingsPath -Destination $backup
$settings.permissions | Add-Member -Force allow $merged
$temporary = $settingsPath + '.clipboard-search.tmp'
[IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $settingsPath -Force
Write-Output "Added archive-scoped read-only search rules. Backup: $backup"
