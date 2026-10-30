[CmdletBinding()]
param([ValidateSet('On', 'Off', 'Read')][string]$State = 'Read')
# Runs as SYSTEM from Program Files (tasks "PersonalTools Power LED <State>", started when Personal
# Tools writes Application event 7101/7102/7103 from the PersonalTools source). Lenovo exposes the
# power button LED only through LENOVO_OTHER_METHOD feature 0x06040000, which needs administrator
# rights. The firmware (Legion 9 18IAX10, BIOS RZCN50WW) takes 0, 1 or 2 and stores anything else
# as 0. Off writes 0; On puts back the value the LED had before Off (1, the firmware default, when
# none was saved). The result goes to power-led-status.txt beside this script (admin-only).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$directory = Split-Path -Parent $PSCommandPath
$statusPath = Join-Path $directory 'power-led-status.txt'
$onPath = Join-Path $directory 'power-led-on-value.txt'
$feature = [uint32]0x06040000
$utf8 = New-Object Text.UTF8Encoding($false)
function Write-Status([string]$Line) {
    $temp = "$statusPath.$PID.tmp"
    [IO.File]::WriteAllText($temp, $Line + [Environment]::NewLine, $utf8)
    # The app reads the file with delete sharing, but another reader could hold it for a moment.
    for ($attempt = 1; ; $attempt++) {
        try { Move-Item -LiteralPath $temp -Destination $statusPath -Force; return }
        catch { if ($attempt -ge 10) { throw }; Start-Sleep -Milliseconds 50 }
    }
}
# The Off, On and Read tasks are separate and could run at the same time; one at a time keeps the
# LED and the status file in the order the requests came.
$lock = New-Object Threading.Mutex($false, 'Global\PersonalTools.PowerLed')
$owned = $false
try {
    try { $owned = $lock.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another power button LED request is still running.' }
    $method = @(Get-CimInstance -Namespace root\WMI -ClassName LENOVO_OTHER_METHOD) | Select-Object -First 1
    if (-not $method) { throw 'LENOVO_OTHER_METHOD is not available on this PC.' }
    function Get-Led { [int](Invoke-CimMethod -InputObject $method -MethodName GetFeatureValue -Arguments @{ IDs = $feature }).value }
    function Set-Led([int]$Value) { $null = Invoke-CimMethod -InputObject $method -MethodName SetFeatureValue -Arguments @{ IDs = $feature; value = [uint32]$Value } }
    $before = Get-Led
    if ($State -eq 'Off' -and $before -ne 0) {
        [IO.File]::WriteAllText($onPath, "$before", $utf8)
        Set-Led 0
    }
    elseif ($State -eq 'On' -and $before -eq 0) {
        $value = 1
        if (Test-Path -LiteralPath $onPath) {
            $saved = 0
            if ([int]::TryParse(([IO.File]::ReadAllText($onPath)).Trim(), [ref]$saved) -and $saved -in 1, 2) { $value = $saved }
        }
        Set-Led $value
    }
    $after = Get-Led
    Write-Status ('value={0} before={1} state={2} at={3}' -f $after, $before, $State, (Get-Date -Format o))
}
catch {
    $message = ($_.Exception.Message -replace '[\r\n]+', ' ').Trim()
    try { Write-Status ('error={0} at={1}' -f $message, (Get-Date -Format o)) } catch { }
    exit 1
}
finally {
    if ($owned) { $lock.ReleaseMutex() }
    $lock.Dispose()
}
