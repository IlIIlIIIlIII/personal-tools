[CmdletBinding()]
param([string]$Executable = "$env:ProgramFiles\PersonalTools\ui\PersonalToolsWindows.exe")
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $Executable -Argument '--startup' -WorkingDirectory (Split-Path $Executable)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
# Give Explorer time to initialize the notification area after sign-in.
$trigger.Delay = 'PT15S'
# Every minute too, so the app comes back when something ends it from outside (2026-10-26). IgnoreNew
# skips these runs while the app this task started is running; a --startup run also leaves at once
# when the app runs anyway, after its Exit command in this sign-in, or before the taskbar is up.
$restart = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'Personal Tools' -Action $action -Trigger @($trigger, $restart) -Principal $principal -Settings $settings -Description 'Start Personal Tools in the system tray after sign-in, and again within a minute if it stops without its Exit command.' -Force | Out-Null
# Remove the old entry only after the replacement task has been registered.
foreach ($oldName in 'SoundControl', 'PersonalTools') {
    Unregister-ScheduledTask -TaskName $oldName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $oldName -ErrorAction SilentlyContinue
}
Write-Output "Personal Tools will start in the notification area at sign-in: $Executable"
