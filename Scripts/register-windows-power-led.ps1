[CmdletBinding()]
param()
# Registers the SYSTEM tasks behind the lighting page's power button switch (run elevated, once). Each
# task runs only set-windows-power-led.ps1 from Program Files with a fixed -State; Personal Tools
# (the signed-in user) starts them by writing Application event 7101 (Off), 7102 (On) or 7103 (Read)
# from the PersonalTools source, the same way the NPU host starts microphone recovery. PCs without
# Lenovo's WMI interface are skipped.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as administrator.' }
if (-not (Get-CimClass -Namespace root\WMI -ClassName LENOVO_OTHER_METHOD -ErrorAction SilentlyContinue)) {
    Write-Output 'Power button LED helper skipped: this PC has no Lenovo LENOVO_OTHER_METHOD interface.'
    return
}
$directory = Join-Path $env:ProgramFiles 'PersonalTools'
$script = Join-Path $directory 'set-windows-power-led.ps1'
# From a package or a checkout, the helper is copied into Program Files first.
foreach ($name in 'set-windows-power-led.ps1', 'register-windows-power-led.ps1') {
    $source = Join-Path $PSScriptRoot $name
    $target = Join-Path $directory $name
    if ((Test-Path -LiteralPath $source) -and [IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($target)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}
if (-not (Test-Path -LiteralPath $script)) { throw "Missing $script" }
if (-not [Diagnostics.EventLog]::SourceExists('PersonalTools')) { New-EventLog -LogName Application -Source 'PersonalTools' }
$class = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler
$settings = New-ScheduledTaskSettingsSet -MultipleInstances Queue -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
$powershell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
foreach ($entry in @(@{ State = 'Off'; Id = 7101 }, @{ State = 'On'; Id = 7102 }, @{ State = 'Read'; Id = 7103 })) {
    $trigger = New-CimInstance -CimClass $class -ClientOnly
    $trigger.Enabled = $true
    $trigger.Subscription = '<QueryList><Query Id="0" Path="Application"><Select Path="Application">*[System[Provider[@Name=''PersonalTools''] and EventID=' + $entry.Id + ']]</Select></Query></QueryList>'
    $triggers = @($trigger)
    # The LED state is read again 30 s after every Windows start (Lenovo's WMI provider is up by then).
    if ($entry.State -eq 'Read') { $boot = New-ScheduledTaskTrigger -AtStartup; $boot.Delay = 'PT30S'; $triggers += $boot }
    $action = New-ScheduledTaskAction -Execute $powershell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $script + '" -State ' + $entry.State)
    Register-ScheduledTask -TaskName "PersonalTools Power LED $($entry.State)" -Action $action -Trigger $triggers -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
}
# The page shows the switch once the helper has reported.
& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script -State Read
Write-Output "Power button LED helper registered (Application events 7101 off, 7102 on, 7103 read): $(Get-Content -LiteralPath (Join-Path $directory 'power-led-status.txt') -Raw)".Trim()
