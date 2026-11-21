param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$OutputPath,
    [ValidateRange(30,600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$exe = Join-Path $appRoot 'StealthWorkspace.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Published executable is missing.' }
if (!$OutputPath) { $OutputPath = Join-Path (Split-Path $appRoot -Parent) ('core-test-' + [Guid]::NewGuid().ToString('N') + '.txt') }
$reportPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $reportPath) { throw 'Test output must be a new file; refusing stale results.' }
New-Item -ItemType Directory -Path (Split-Path $reportPath -Parent) -Force | Out-Null
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $exe
$startInfo.WorkingDirectory = $appRoot
# A headless build test is a child process, not an Explorer/Shell launch. Use
# ArgumentList to preserve Unicode/space-containing paths without shell parsing.
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
if ($null -ne $startInfo.PSObject.Properties['ArgumentList']) {
    $startInfo.ArgumentList.Add('--self-test')
    $startInfo.ArgumentList.Add($reportPath)
} else {
    $escapedReport = '"' + ($reportPath -replace '"', '\"') + '"'
    $startInfo.Arguments = "--self-test $escapedReport"
}
$testProcess = [Diagnostics.Process]::Start($startInfo)
if (!$testProcess.WaitForExit($TimeoutSeconds * 1000)) {
    $testProcess.Kill()
    throw "Core tests timed out after $TimeoutSeconds seconds."
}
$testProcess.Refresh()
if (!(Test-Path -LiteralPath $reportPath)) { throw "No test report (exit $($testProcess.ExitCode))." }
$report = Get-Content -LiteralPath $reportPath -Raw
if ($testProcess.ExitCode -ne 0 -or $report -notmatch 'Passed: [1-9][0-9]*  Failed: 0' -or $report -match '(?m)^FAIL\s') {
    Write-Output $report
    throw "Core tests failed (exit $($testProcess.ExitCode))."
}
Write-Output (($report -split '\r?\n')[1])
Write-Output "Report: $reportPath"
