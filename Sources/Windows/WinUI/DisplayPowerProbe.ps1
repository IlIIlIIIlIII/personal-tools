param(
    [Parameter(Mandatory)][ValidatePattern('^PersonalTools\.DisplayPower\.[a-f0-9]{32}$')][string]$PipeName,
    [Parameter(Mandatory)][int]$ParentId,
    [Parameter(Mandatory)][long]$ParentStartTicks
)
$ErrorActionPreference = 'Stop'
# This elevated helper only runs the fixed Windows query. It never reads IPC commands.
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::Out)
try {
    $pipe.Connect(15000)
    while ($true) {
        $parentProcess = Get-Process -Id $ParentId -ErrorAction Stop
        if ($parentProcess.StartTime.ToUniversalTime().Ticks -ne $ParentStartTicks) { break }
        $query = $null
        try {
            $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\powercfg.exe'), '/requests')
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $query = [Diagnostics.Process]::Start($info)
            $stdout = $query.StandardOutput.ReadToEndAsync()
            $stderr = $query.StandardError.ReadToEndAsync()
            if (-not $query.WaitForExit(10000)) { $query.Kill(); throw 'Windows power query timed out.' }
            $result = @{ ExitCode = $query.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $null }
            $errorText = $stderr.GetAwaiter().GetResult()
            if ($query.ExitCode -ne 0 -and $errorText) { $result.Error = $errorText }
        } catch {
            $result = @{ ExitCode = -1; Output = ''; Error = $_.Exception.Message }
        } finally { if ($null -ne $query) { $query.Dispose() } }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Compress))
        if ($bytes.Length -gt 1048576) { throw 'Power query response too large.' }
        $header = [BitConverter]::GetBytes([int]$bytes.Length)
        $pipe.Write($header, 0, 4)
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        Start-Sleep -Seconds 5
    }
} finally { $pipe.Dispose() }
