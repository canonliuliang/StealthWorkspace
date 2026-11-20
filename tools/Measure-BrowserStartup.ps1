#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [ValidateRange(5,50)][int]$Pairs = 5,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot ('../artifacts/startup-' + [guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory to preserve existing reports.' }
if (Get-Process StealthWorkspace -ErrorAction SilentlyContinue) { throw 'An existing Stealth window is running.' }

function Read-StartupPackage([string]$Directory) {
    $root = (Resolve-Path -LiteralPath $Directory).Path
    $metadata = Get-Content (Join-Path $root 'release.json') -Raw | ConvertFrom-Json
    $runtimeConfig = Get-Content (Join-Path $root 'StealthWorkspace.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtime = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App'
    if (!$metadata.runtime -or !$metadata.version -or !$runtime.version) { throw "Missing self-contained package metadata in $root" }
    [pscustomobject]@{
        Root = $root; Version = $metadata.version; Runtime = $metadata.runtime; DotNet = $runtime.version
        ExeSha256 = (Get-FileHash -LiteralPath (Join-Path $root 'StealthWorkspace.exe') -Algorithm SHA256).Hash
        DllSha256 = (Get-FileHash -LiteralPath (Join-Path $root 'StealthWorkspace.dll') -Algorithm SHA256).Hash
    }
}
$packages = @{baseline = (Read-StartupPackage $BaselineDirectory); candidate = (Read-StartupPackage $CandidateDirectory)}
if ($packages.baseline.Runtime -ne $packages.candidate.Runtime) { throw 'Compare packages for the same architecture.' }
New-Item -ItemType Directory -Path $output | Out-Null

function Get-MarkerMilliseconds([string]$Report, [string]$Marker, [datetime]$Started) {
    $pattern = '(?m)^(\d{2}:\d{2}:\d{2}\.\d{3})  ' + [regex]::Escape($Marker)
    $found = [regex]::Matches($Report, $pattern)
    if ($found.Count -ne 1) { throw "Expected exactly one $Marker marker." }
    $stamp = [timespan]::ParseExact($found[0].Groups[1].Value, 'hh\:mm\:ss\.fff', [cultureinfo]::InvariantCulture)
    $elapsed = ($stamp - $Started.TimeOfDay).TotalMilliseconds
    if ($elapsed -lt -43200000) { $elapsed += [timespan]::FromDays(1).TotalMilliseconds }
    if ($elapsed -lt -1 -or $elapsed -gt 60000) { throw "Inconsistent process and diagnostic clocks at $Marker." }
    [math]::Round($elapsed, 1)
}

function Measure-Launch([string]$Build, [int]$Pair) {
    $package = $packages[$Build]
    $testDir = Join-Path $output ("{0:D2}-{1}" -f $Pair, $Build)
    $temp = Join-Path $testDir 'temp'
    New-Item -ItemType Directory -Path $temp | Out-Null
    $reportPath = Join-Path $testDir 'smoke.txt'
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $package.Root 'StealthWorkspace.exe'))
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $package.Root
    $info.Environment['TEMP'] = $temp
    $info.Environment['TMP'] = $temp
    foreach ($arg in @('--smoke-test', $reportPath, '--data-root', (Join-Path $testDir 'data'))) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $started = $process.StartTime
        if (!$process.WaitForExit(60000)) {
            $null = $process.CloseMainWindow()
            if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
            throw "Smoke process timed out. Inspect $testDir"
        }
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8
        $required = @('SMOKE RESULT: OK', 'lockPageVisible=True', 'closing: vault locked=True',
            'closing: browser controls released', 'closing: browser profiles removed=True')
        if ($process.ExitCode -ne 0 -or @($required | Where-Object { !$report.Contains($_) }).Count -or
            $report -match 'unhandled-ui-exception|launch-failed|closing:[^\r\n]*(?:locked=False|removed=False|failed|失败|超时)') {
            throw "Startup or shutdown validation failed. Inspect $reportPath"
        }
        $launched = Get-MarkerMilliseconds $report 'OnLaunched:' $started
        $ready = Get-MarkerMilliseconds $report 'UI ready: page=LockPage' $started
        if ($ready -lt $launched) { throw "Unexpected startup marker order in $reportPath" }
        [pscustomobject]@{
            Pair = $Pair; Warmup = ($Pair -eq 0); Build = $Build; Version = $package.Version
            ProcessStartedUtc = $started.ToUniversalTime().ToString('o'); ProcessToOnLaunchedMs = $launched
            ProcessToLockPageReadyMs = $ready; OnLaunchedToLockPageReadyMs = [math]::Round($ready - $launched, 1)
        }
    } finally { $process.Dispose() }
}

$samples = foreach ($pair in 0..$Pairs) {
    $order = if ($pair % 2 -eq 0) { 'baseline', 'candidate' } else { 'candidate', 'baseline' }
    foreach ($build in $order) {
        $sample = Measure-Launch $build $pair
        $sample | Export-Csv -LiteralPath (Join-Path $output 'samples.csv') -Append -NoTypeInformation
        $sample
    }
}
$summary = foreach ($build in 'baseline','candidate') {
    $values = @($samples | Where-Object { $_.Build -eq $build -and !$_.Warmup } |
        Sort-Object ProcessToLockPageReadyMs | ForEach-Object ProcessToLockPageReadyMs)
    $middle = [int][math]::Floor($values.Length / 2)
    $median = if ($values.Length % 2) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }
    [pscustomobject]@{ Build=$build; Samples=$values.Length; MinMs=$values[0]; MedianMs=$median; MaxMs=$values[-1] }
}
$computer = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
[pscustomobject]@{
    Scope = 'OS process creation to locked WinUI XAML Root.Loaded diagnostic marker. Fresh synthetic data root, warm binary caches. No WebView or first webpage is created.'
    System = $os.Caption; SystemBuild = $os.BuildNumber; Machine = $computer.Model
    LogicalProcessors = $computer.NumberOfLogicalProcessors; MemoryBytes = $computer.TotalPhysicalMemory
    ClockResolution = 'Diagnostic markers use time of day with millisecond precision.'
    Packages = $packages; Summary = $summary; Samples = $samples
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
$summary | Format-Table -AutoSize
Write-Output "Startup results: $output"
