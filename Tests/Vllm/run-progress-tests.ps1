[CmdletBinding()]
param(
    [string]$Distribution = 'Ubuntu-26.04',
    [string]$LinuxUser = 'sunggu',
    [string]$Python = '/home/sunggu/Projects/Personal/llm/.venv/bin/python'
)
$ErrorActionPreference = 'Stop'
$vllmTestRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$vllmFixture = @{
    observer = Get-Content (Join-Path $vllmTestRoot 'Sources/Windows/WinUI/VllmStart.py') -Raw
    tests = Get-Content (Join-Path $PSScriptRoot 'test_progress_bridge.py') -Raw
} | ConvertTo-Json -Compress
$vllmFixture | wsl -d $Distribution -u $LinuxUser -- $Python -c 'import sys,json; data=json.load(sys.stdin); observer_source=data["observer"]; exec(compile(data["tests"], "test_progress_bridge.py", "exec"))'
if ($LASTEXITCODE -ne 0) { throw 'vLLM progress observer tests failed.' }
