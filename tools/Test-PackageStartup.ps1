param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $AppDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Choose a new startup report directory: $output" }
$null = [IO.Directory]::CreateDirectory($output)
$report = Join-Path $output 'startup.txt'
$fixture = Join-Path $output 'isolated-data'
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'StealthWorkspace.exe'))
$info.UseShellExecute = $false
$info.WorkingDirectory = $root
# Quoted Windows file paths cannot contain double quotes; both end in a filename.
$info.Arguments = '--smoke-test "' + $report + '" --data-root "' + $fixture + '"'
$child = [Diagnostics.Process]::Start($info)
try {
    if (!$child.WaitForExit(45000)) {
        $null = $child.CloseMainWindow()
        if (!$child.WaitForExit(3000)) { $child.Kill(); $child.WaitForExit() }
        throw "Startup check timed out: $report"
    }
    $result = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw -Encoding UTF8 } else { '' }
    if ($child.ExitCode -ne 0 -or $result -notmatch 'UI ready: page=LockPage[^\r\n]*lockPageVisible=True' -or
        $result -notmatch 'SMOKE RESULT: OK' -or $result -notmatch 'closing: browser profiles removed=True' -or
        $result -match 'launch-failed:|unhandled-ui-exception:') {
        throw "The packaged WinUI lock page did not open and exit cleanly (exit $($child.ExitCode)): $report"
    }
    Write-Output "Verified packaged window: $report"
} finally { $child.Dispose() }
