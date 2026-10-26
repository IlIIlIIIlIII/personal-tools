[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsInstall.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WindowsDeviceRecovery.psm1') -Force
Assert-Administrator
$directory = Join-Path $env:ProgramFiles 'PersonalTools'
$backupPath = Join-Path $directory 'installation-backup.clixml'
$backup = Import-Clixml -LiteralPath $backupPath
if ($backup.Version -ne 1) { throw 'Unsupported installation backup.' }
if (-not $backup.PSObject.Properties['RenderIdentity']) {
    $identity = Get-RenderIdentity $backup.RenderId
    $backup | Add-Member -NotePropertyName RenderIdentity -NotePropertyValue $identity
    Save-RecoveryBackup $backup $backupPath
}
$script = Join-Path $directory 'repair-windows-device.ps1'
if (-not (Test-Path -LiteralPath $script)) { throw 'Recovery payload is not installed.' }
# The elevated action and its identity/rollback metadata live in Program Files,
# never in the writable settings folder. Task has no arbitrary command inputs.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $script + '"')
$triggers = @((New-ScheduledTaskTrigger -AtStartup), (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)))
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
Register-ScheduledTask -TaskName 'PersonalTools Device Recovery' -Action $action -Trigger $triggers -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
Write-Output 'Device recovery registered: startup and once per minute; audio restarts only when an endpoint changes.'

# Microphone recovery (repair-windows-microphone.ps1): the USB device behind the selected
# microphone is resolved now, while it works; PnP queries can take minutes once its stream is stuck.
$microphone = Join-Path $directory 'repair-windows-microphone.ps1'
if (Test-Path -LiteralPath $microphone) {
    if (-not $backup.PSObject.Properties['CaptureDevice'] -or -not $backup.CaptureDevice) {
        $device = $null
        try { $device = Get-CaptureDevice $backup.CaptureId } catch { Write-Warning "Microphone recovery is not available: $_" }
        if ($device) { $backup | Add-Member -NotePropertyName CaptureDevice -NotePropertyValue $device -Force; Save-RecoveryBackup $backup $backupPath }
    }
    if ($backup.PSObject.Properties['CaptureDevice'] -and $backup.CaptureDevice) {
        # The NPU host (signed-in user) reports a stall as Application event 7001 from this source.
        if (-not [Diagnostics.EventLog]::SourceExists('PersonalTools')) { New-EventLog -LogName Application -Source 'PersonalTools' }
        $class = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler
        $stall = New-CimInstance -CimClass $class -ClientOnly
        $stall.Enabled = $true
        $stall.Subscription = '<QueryList><Query Id="0" Path="Application"><Select Path="Application">*[System[Provider[@Name=''PersonalTools''] and EventID=7001]]</Select></Query></QueryList>'
        $micAction = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $microphone + '"')
        $micTriggers = @($stall, (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)))
        $micSettings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 3)
        Register-ScheduledTask -TaskName 'PersonalTools Microphone Recovery' -Action $micAction -Trigger $micTriggers -Settings $micSettings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
        Write-Output "Microphone recovery registered for $($backup.CaptureDevice): on a stall event and every 5 minutes."
    }
}
