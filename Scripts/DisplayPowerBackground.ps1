$ErrorActionPreference = 'Stop'
$resultPath = Join-Path $PSScriptRoot 'result.json'
while ($true) {
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
    $result.CheckedAt = [DateTimeOffset]::UtcNow.ToString('o')
    [IO.File]::WriteAllText("$resultPath.tmp", ($result | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $resultPath) { [IO.File]::Replace("$resultPath.tmp", $resultPath, "$resultPath.bak") }
    else { [IO.File]::Move("$resultPath.tmp", $resultPath) }
    Start-Sleep -Seconds 5
}
