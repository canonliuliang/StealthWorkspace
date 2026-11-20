param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('Smoke','Render','Browser','Experience','Soak','Web')][string]$Mode = 'Smoke',
    [string]$OutputDirectory,
    [ValidateSet('Dark','Light')][string]$Theme = 'Dark',
    [ValidateRange(480,2000)][int]$Width = 1100,
    [ValidateRange(480,1600)][int]$Height = 740,
    [ValidateRange(1,120)][int]$SoakMinutes = 3,
    [switch]$ReducedMotion,
    [uri]$Url
)
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'Web' -and (!$Url -or !$Url.IsAbsoluteUri -or $Url.Scheme -notin 'http','https' -or $Url.UserInfo)) {
    throw 'Web requires an absolute HTTP(S) test URL without embedded credentials.'
}
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
$exe = Join-Path $appRoot 'StealthWorkspace.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Application executable is missing.' }
if (Get-Process StealthWorkspace -ErrorAction SilentlyContinue) { throw 'Close the existing Stealth window before running isolated UI tests.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('../artifacts/ui-' + $Mode.ToLowerInvariant() + '-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; previous reports are preserved.' }
New-Item -ItemType Directory -Path $output | Out-Null
$report = Join-Path $output 'result.txt'
$info = [Diagnostics.ProcessStartInfo]::new($exe)
$info.UseShellExecute = $false
$info.WorkingDirectory = $appRoot
foreach ($argument in @('--ui-smoke',$report,'--data-root',(Join-Path $output 'data'),'--ui-theme',$Theme,'--ui-width',"$Width",'--ui-height',"$Height")) { $info.ArgumentList.Add($argument) }
$marker = 'SMOKE RESULT: OK'
if ($ReducedMotion) { $info.ArgumentList.Add('--ui-reduced-motion') }
switch ($Mode) {
    'Render' { $info.ArgumentList.Add('--browser-render-audit'); $marker = 'RENDER AUDIT: OK' }
    'Browser' { $info.ArgumentList.Add('--browser-audit'); $marker = 'AUDIT RESULT: OK' }
    'Experience' { $info.ArgumentList.Add('--experience-test'); $marker = 'EXPERIENCE RESULT: OK' }
    'Soak' { $info.ArgumentList.Add('--browser-soak-minutes'); $info.ArgumentList.Add("$SoakMinutes"); $marker = 'SOAK RESULT: OK' }
    'Web' { $info.ArgumentList.Add('--browser-probe-url'); $info.ArgumentList.Add($Url.AbsoluteUri); $marker = 'NETWORK PROBE: completed' }
}
$process = [Diagnostics.Process]::Start($info)
$timeout = if ($Mode -eq 'Soak') { ($SoakMinutes + 2) * 60 } else { 240 }
Write-Output "Running $Mode in isolated test data; PID=$($process.Id)"
if (!$process.WaitForExit($timeout * 1000)) {
    $null = $process.CloseMainWindow()
    if (!$process.WaitForExit(5000)) { $process.Kill() }
    throw "UI test exceeded $timeout seconds. Evidence: $output"
}
if (!(Test-Path -LiteralPath $report)) { throw "No UI report; exit code $($process.ExitCode)." }
$result = Get-Content -LiteralPath $report -Raw
$requiredCleanup = @('closing: vault locked=True', 'closing: browser controls released', 'closing: browser profiles removed=True')
$missingCleanup = @($requiredCleanup | Where-Object { !$result.Contains($_) })
$cleanupFailed = $result -match 'closing:[^\r\n]*(?:locked=False|removed=False|failed|failure|超时|失败)'
if ($process.ExitCode -ne 0 -or !$result.Contains($marker) -or
    $result -match 'ui-smoke failed:|UnhandledException|UNHANDLED' -or $cleanupFailed -or $missingCleanup.Count -ne 0) {
    throw "UI test failed. Inspect $report"
}
Write-Output "$marker — $report"
if ($Mode -eq 'Web') {
    $lines = Get-Content -LiteralPath $report
    $sample = $lines | Where-Object { $_ -match ' probe: "\{' } | Select-Object -Last 1
    if (!$sample -or $result -notmatch 'success=True; HTTP=2\d\d;') { throw "No successful website navigation. Inspect $report" }
    $outer = $sample.Substring($sample.IndexOf(' probe: ') + 8) | ConvertFrom-Json
    $metrics = $outer | ConvertFrom-Json
    if ($metrics.ready -ne 'complete' -or !$metrics.navigation -or $metrics.origin -notmatch '^https?://') { throw "Document did not finish loading. Inspect $report" }
    $metrics | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $output 'navigation.json')
    Write-Output "Page loaded: $($metrics.origin), resources=$($metrics.resources)"
}
