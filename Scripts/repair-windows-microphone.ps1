# Fixed, protected scheduled-task action (SYSTEM). No user-supplied command/path/device.
# Restarts the selected microphone's USB device when its input has stalled: since 2026-10-24 the
# MOTU M Series locks up in Call of Duty's loading hiccups, repeating its last buffer (or zeros)
# until it is replugged. Started by the NPU host's event 7001 (again every minute while stalled)
# and every 5 minutes; it checks the stall itself (PersonalToolsSetup.exe --check-microphone)
# before acting. First it power-cycles the device's hub port (--cycle-port), which is a replug:
# only the microphone drops for ~8 s. Only if that fails does it stop Windows Audio and the vendor
# helpers and restart the device with pnputil, which silences all playback for ~9 s.
[CmdletBinding()]
param([switch]$CheckOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsInstall.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WindowsDeviceRecovery.psm1') -Force
$directory = Join-Path $env:ProgramFiles 'PersonalTools'
$backupPath = Join-Path $directory 'installation-backup.clixml'
$statusPath = Join-Path $directory 'microphone-recovery-status.txt'
$historyPath = Join-Path $directory 'microphone-recovery-history.txt'
$logPath = Join-Path $directory 'microphone-recovery.log'
if (-not $CheckOnly) { Assert-Administrator }
if (-not (Test-Path -LiteralPath $backupPath)) { return }
# One line per run (the stall event or the 5-minute check) and what it found; the last 1000 are kept.
function Write-RunLog([string]$Text) {
    if ($CheckOnly) { return }
    if ((Test-Path -LiteralPath $logPath) -and (Get-Item -LiteralPath $logPath).Length -gt 256KB) {
        [IO.File]::WriteAllLines($logPath, [string[]](Get-Content -LiteralPath $logPath | Select-Object -Last 1000))
    }
    Add-Content -LiteralPath $logPath -Encoding utf8 -Value ("{0:s} {1}" -f (Get-Date), $Text)
}
# The app reads this file and shows a notification for each new line it starts with.
function Report([string]$Message) {
    Write-Output $Message
    if (-not $CheckOnly) { [IO.File]::WriteAllText($statusPath, $Message, [Text.UTF8Encoding]::new($false)) }
    Write-RunLog $Message
}
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    $process.ExitCode
}
$lock = $null
try {
    if (-not $CheckOnly) { $lock = [IO.File]::Open((Join-Path $directory 'microphone-recovery.lock'),'OpenOrCreate','ReadWrite','None') }
    $backup = Import-Clixml -LiteralPath $backupPath
    if ($backup.Version -ne 1) { throw 'Unsupported installation backup.' }
    if (-not $backup.PSObject.Properties['CaptureDevice'] -or -not $backup.CaptureDevice) { throw 'Register device recovery first.' }
    $device = [string]$backup.CaptureDevice
    # 0 healthy, 3 no microphone stream, 4 no APO section: nothing to do.
    $check = Invoke-Checked (Join-Path $directory 'PersonalToolsSetup.exe') @('--check-microphone')
    Write-RunLog "check $check"
    if ($check -ne 10) { return }
    $now = Get-Date
    $history = @()
    if (Test-Path -LiteralPath $historyPath) {
        $history = @(Get-Content -LiteralPath $historyPath | ForEach-Object { [datetime]::Parse($_, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) } |
            Where-Object { ($now - $_).TotalHours -lt 1 })
    }
    # A device that stalls again right away needs the cable, not a restart loop.
    if ($history.Count -gt 0 -and ($now - ($history | Sort-Object | Select-Object -Last 1)).TotalSeconds -lt 90) { Write-RunLog 'stalled, but restarted less than 90 seconds ago'; return }
    if ($history.Count -ge 4) {
        # The host repeats event 7001 every minute while the input stays stalled; one notice per 15 minutes is enough.
        if ((Test-Path -LiteralPath $statusPath) -and [IO.File]::ReadAllText($statusPath).StartsWith('Microphone recovery paused') -and
            ([datetime]::UtcNow - (Get-Item -LiteralPath $statusPath).LastWriteTimeUtc).TotalMinutes -lt 15) { Write-RunLog 'stalled, recovery paused (already reported)'; return }
        Report ("Microphone recovery paused at {0}: the input stalled again after 4 restarts within an hour. Replug the USB cable." -f $now.ToString('o')); return
    }
    if ($CheckOnly) { Report ("Would restart {0}: the microphone input has stalled." -f $device); return }
    [IO.File]::WriteAllLines($historyPath, [string[]](@($history | ForEach-Object { $_.ToString('o') }) + $now.ToString('o')))
    Report ("Recovering microphone at {0}: the input stalled; reconnecting {1}." -f $now.ToString('o'), $device)
    # 0: back; 20/21: device or hub not found; 22: the hub refused; 23: not back in 30 s;
    # 24: back, but not every interface/endpoint started yet (no reason to stop Windows Audio).
    $cycle = Invoke-Checked (Join-Path $directory 'PersonalToolsSetup.exe') @('--cycle-port', "`"$device`"")
    Write-RunLog "cycle port $cycle"
    if ($cycle -eq 0 -or $cycle -eq 24) { Report ("Recovered microphone at {0} by reconnecting its USB port." -f (Get-Date -Format o)); return }
    Report ("Recovering microphone at {0}: reconnecting the USB port failed ({1}); restarting Windows Audio and {2}." -f (Get-Date -Format o), $cycle, $device)
    $folder = Get-DeviceHelperFolder $device
    $closed = @(); $stopped = @()
    $audio = Get-Service Audiosrv
    $dependents = @($audio.DependentServices | Where-Object Status -eq 'Running')
    $code = -1
    try {
        if ($folder) {
            # Services first: a killed service process is restarted by the service manager within
            # 0.5 s (MOTUCoreUACAudioPolicyMediator, 2026-10-26) and grabs the device again.
            foreach ($service in @(Get-CimInstance Win32_Service | Where-Object { $_.State -eq 'Running' -and $_.PathName -like "*\$folder\*" })) { Stop-Service -Name $service.Name -Force; $stopped += $service.Name }
            foreach ($process in @(Get-Process | Where-Object { $_.Path -and $_.Path -like "*\$folder\*" })) { $closed += $process.Path; Stop-Process -Id $process.Id -Force }
        }
        Stop-Service -InputObject $audio -Force
        Start-Sleep -Seconds 2
        # 0: restarted; 3010: still held open (a reboot would be needed).
        $code = Invoke-Checked 'pnputil.exe' @('/restart-device', "`"$device`"")
    } finally {
        foreach ($name in $stopped) { Start-Service -Name $name -ErrorAction SilentlyContinue }
        Start-Service -InputObject $audio
        foreach ($service in $dependents) { Start-Service -InputObject $service -ErrorAction SilentlyContinue }
    }
    # The app starts the closed helpers again in the user's session.
    $list = ($closed | Select-Object -Unique) -join '|'
    if ($code -eq 0) { Report ("Recovered microphone at {0}. Closed: {1}" -f (Get-Date -Format o), $list) }
    else { Report ("Microphone recovery failed at {0} (pnputil {1}); replug the USB cable. Closed: {2}" -f (Get-Date -Format o), $code, $list) }
} catch {
    Report ("Microphone recovery needs attention: $_")
    throw
} finally {
    if ($lock) { $lock.Dispose() }
}
