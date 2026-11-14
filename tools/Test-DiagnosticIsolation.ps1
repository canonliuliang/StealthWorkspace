#requires -Version 7.0
<#
Validates diagnostic data isolation only, using a copied published application and
synthetic legacy files. It does not exercise production-data migration, read real
vaults, manipulate the clipboard, or clean up evidence after the test.
#>
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(30,600)][int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath (Join-Path $appRoot 'StealthWorkspace.exe') -PathType Leaf)) { throw 'Published application executable is missing.' }
if (Get-Process StealthWorkspace -ErrorAction SilentlyContinue) { throw 'Close the existing Stealth window before running diagnostic isolation tests.' }
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; previous evidence is preserved.' }
$sourcePrefix = $appRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($output.Equals($appRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must be outside the application directory to prevent recursive copying.'
}
foreach ($privateName in @('StealthVault_Store', 'WorkspaceData', 'MediaVault', 'system_catalog.dat', 'settings.dat')) {
    if (Test-Path -LiteralPath (Join-Path $appRoot $privateName)) { throw "Source contains $privateName. Use a clean published application directory, without user data." }
}
$sourceEntries = @((Get-Item -LiteralPath $appRoot -Force)) + @(Get-ChildItem -LiteralPath $appRoot -Force -Recurse)
if ($sourceEntries | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
    throw 'Application source contains a reparse point; refusing to follow external content.'
}

New-Item -ItemType Directory -Path $output | Out-Null
$install = Join-Path $output 'synthetic-install'
Copy-Item -LiteralPath $appRoot -Destination $install -Recurse -Force
$exe = Join-Path $install 'StealthWorkspace.exe'
$tempRoot = Join-Path $output 'diagnostic-temp'
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$legacy = Join-Path $install 'StealthVault_Store'
New-Item -ItemType Directory -Path (Join-Path $legacy 'EncryptedMedia') | Out-Null
$sentinel = 'diagnostic-isolation-' + [Guid]::NewGuid().ToString('N')
$syntheticSettings = @{
    AppTheme = 'Light'; ClearClipboardOnLock = $false
    Bookmarks = @(@{ Title = $sentinel; Url = "https://$sentinel.invalid/" })
} | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $legacy 'settings.json'), $syntheticSettings)
[IO.File]::WriteAllText((Join-Path $legacy 'vault_config.dat'), 'Synthetic invalid configuration. No personal or encrypted data.')
[IO.File]::WriteAllBytes((Join-Path $legacy 'EncryptedMedia/sentinel.bin'), [Text.Encoding]::UTF8.GetBytes($sentinel))

function Get-LegacyManifest {
    @(Get-ChildItem -LiteralPath $legacy -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            Path = [IO.Path]::GetRelativePath($legacy, $_.FullName)
            Length = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
$before = @(Get-LegacyManifest)
$before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'legacy-before.json') -Encoding utf8
function Assert-LegacyUnchanged([string]$Stage) {
    $after = @(Get-LegacyManifest)
    $after | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output "legacy-after-$Stage.json") -Encoding utf8
    if ($after.Count -ne $before.Count -or @(Compare-Object $before $after -Property Path, Length, Sha256).Count -ne 0) {
        throw "Synthetic legacy files changed during $Stage. Evidence retained at $output"
    }
}
function Invoke-IsolatedDiagnostic([string]$Switch, [string]$Report) {
    if (Get-Process StealthWorkspace -ErrorAction SilentlyContinue) { throw 'Another Stealth process appeared; refusing to run concurrent diagnostics.' }
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = $install
    # This is the child's OS temporary directory, not an explicit app data root.
    # The application must independently choose its normal random diagnostic root.
    $info.Environment['TEMP'] = $tempRoot
    $info.Environment['TMP'] = $tempRoot
    $info.ArgumentList.Add($Switch)
    $info.ArgumentList.Add($Report)
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $null = $process.CloseMainWindow()
            if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
            throw "$Switch timed out. Evidence retained at $output"
        }
        if (!(Test-Path -LiteralPath $Report -PathType Leaf)) { throw "$Switch did not write its report (exit $($process.ExitCode))." }
        $result = Get-Content -LiteralPath $Report -Raw
        if ($process.ExitCode -ne 0) { throw "$Switch exited with code $($process.ExitCode). Inspect $Report" }
        return $result
    }
    finally { $process.Dispose() }
}

$evidence = [ordered]@{
    Scope = 'Diagnostic data isolation only; synthetic legacy files; no production migration or clipboard test.'
    Source = $appRoot
    ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    ChildTemp = $tempRoot
    SmokeReport = (Join-Path $output 'smoke-result.txt')
    SelfTestReport = (Join-Path $output 'selftest-result.txt')
    Status = 'Running'
}
try {
    $smoke = Invoke-IsolatedDiagnostic '--smoke-test' $evidence.SmokeReport
    Assert-LegacyUnchanged 'smoke'
    $requiredCleanup = @('closing: vault locked=True', 'closing: browser controls released', 'closing: browser profiles removed=True')
    $missingCleanup = @($requiredCleanup | Where-Object { !$smoke.Contains($_) })
    if (!$smoke.Contains('SMOKE RESULT: OK') -or $missingCleanup.Count -ne 0 -or
        $smoke -match 'ui-smoke failed:|UnhandledException|UNHANDLED|closing:[^\r\n]*(?:locked=False|removed=False|failed|failure|超时|失败)') {
        throw "Bare smoke or its shutdown cleanup failed. Inspect $($evidence.SmokeReport)"
    }
    $diagnostics = Join-Path $tempRoot 'StealthWorkspace.Diagnostics'
    if (!(Test-Path -LiteralPath $diagnostics -PathType Container)) { throw 'Bare smoke did not create its automatic diagnostic root.' }
    $roots = @(Get-ChildItem -LiteralPath $diagnostics -Directory -Force)
    if ($roots.Count -ne 1 -or $roots[0].Name -notmatch '^[0-9a-fA-F]{32}$') { throw 'Expected exactly one newly generated random diagnostic directory.' }
    $isolatedRoot = $roots[0].FullName
    $isolatedSettings = Join-Path $isolatedRoot 'settings.dat'
    if (!(Test-Path -LiteralPath $isolatedSettings -PathType Leaf)) { throw 'Automatic diagnostic root has no isolated settings.' }
    # Only decrypt the new fixture's settings, never any existing app settings.
    $plaintext = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($isolatedSettings), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try {
        $settingsText = [Text.Encoding]::UTF8.GetString($plaintext)
        $settings = $settingsText | ConvertFrom-Json
        if ($null -eq $settings -or $settingsText.Contains($sentinel)) { throw 'Diagnostic settings imported the synthetic legacy bookmark.' }
    }
    finally { [Array]::Clear($plaintext, 0, $plaintext.Length) }
    if ((Test-Path -LiteralPath (Join-Path $isolatedRoot 'system_catalog.dat')) -or
        @(Get-ChildItem -LiteralPath $isolatedRoot -File -Recurse -Force | Where-Object { $_.Name -in @('sentinel.bin', 'vault_config.dat', 'settings.json') }).Count -ne 0) {
        throw 'Bare smoke imported the synthetic legacy vault into its diagnostic root.'
    }
    $evidence.AutomaticDataRoot = $isolatedRoot
    $evidence.IsolatedSettingsSha256 = (Get-FileHash -LiteralPath $isolatedSettings -Algorithm SHA256).Hash
    $evidence.BareSmoke = 'Passed; automatic root, no legacy import, normal exit and all shutdown cleanup facts verified.'

    $core = Invoke-IsolatedDiagnostic '--self-test' $evidence.SelfTestReport
    Assert-LegacyUnchanged 'selftest'
    if ($core -notmatch '(?m)^Passed: [1-9][0-9]*  Failed: 0(?:  Skipped: [0-9]+)?\s*$' -or $core -match '(?m)^FAIL\s') {
        throw "Self-test failed. Inspect $($evidence.SelfTestReport)"
    }
    $evidence.SelfTest = ($core -split '\r?\n' | Where-Object { $_ -match '^Passed:' }) -join ''
    $evidence.LegacySentinelsUnchanged = $true
    $evidence.Status = 'Passed'
    Write-Output "Diagnostic isolation passed; bare smoke and self-test preserved all synthetic legacy files. Evidence: $output"
}
catch {
    $evidence.Status = 'Failed'
    $evidence.Failure = $_.Exception.Message
    throw
}
finally {
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'isolation-result.json') -Encoding utf8
}
