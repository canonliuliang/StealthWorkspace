param(
    [Parameter(Mandatory = $true)][string]$CandidateDirectory,
    [string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$PublishDirectory) { $PublishDirectory = Join-Path $projectRoot 'bin\Release\net8.0-windows10.0.26100.0\win-arm64\publish' }
$candidateRoot = (Resolve-Path -LiteralPath $CandidateDirectory).Path.TrimEnd('\')
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
$workspacePrefix = $projectRoot.TrimEnd('\') + '\'
foreach ($path in @($candidateRoot, $publishRoot)) {
    if (!$path.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release directories must be inside this project.' }
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release directories must not be junctions or symbolic links.' }
}
if ($candidateRoot.Equals($publishRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $candidateRoot.StartsWith($publishRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $publishRoot.StartsWith($candidateRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate and installed directories must be separate.' }

# Use the app's production single-instance lease. A second launch during the
# copy must not load a mixture of old and new DLLs. Diagnostic instances have
# distinct leases, as defined in Services/SingleInstance.cs.
$createdLease = $false
$updateLease = [Threading.Mutex]::new($true, 'Local\StealthWorkspace.Instance.v1', [ref]$createdLease)
if (!$createdLease) {
    $updateLease.Dispose()
    throw 'The app is running or another update owns its lease. Close the installed window normally before updating.'
}
try {
$exe = Join-Path $publishRoot 'StealthWorkspace.exe'
if (Get-Process -Name StealthWorkspace -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'The installed app is still running. Close its window normally before updating; this script never terminates a browser session.'
}
foreach ($required in @('StealthWorkspace.exe', 'StealthWorkspace.dll', 'StealthWorkspace.deps.json',
    'StealthWorkspace.runtimeconfig.json', 'StealthWorkspace.pri', 'Assets\AppIcon.ico', 'Assets\AppIcon.png')) {
    if (!(Test-Path -LiteralPath (Join-Path $candidateRoot $required) -PathType Leaf)) { throw "Incomplete candidate: $required" }
}

# Copy only the candidate's program files. Never mirror, clean, move or remove
# the installed folder, which can contain the user's portable WorkspaceData.
$files = @(Get-ChildItem -LiteralPath $candidateRoot -File -Recurse -Force | Sort-Object FullName)
$entries = foreach ($file in $files) {
    $relative = $file.FullName.Substring($candidateRoot.Length + 1)
    if ($relative -match '(^|[\\/])(WorkspaceData|MediaVault|IsolatedDownloads|Stealth_WebProfile_[^\\/]*|StealthVault_Store)([\\/]|$)' -or
        $relative -match '(^|[\\/])(system_catalog\.dat|settings\.dat|app\.log)(\.|$)') { throw "Candidate contains user data: $relative" }
    $target = [IO.Path]::GetFullPath((Join-Path $publishRoot $relative))
    if (!$target.StartsWith($publishRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid release file path.' }
    # Check every existing ancestor, not just the top-level destination.
    foreach ($start in @($file.FullName, $target)) {
        $ancestor = $start
        while ($ancestor.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Release path traverses a junction or symbolic link: $ancestor"
            }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
    }
    [pscustomobject]@{ Relative = $relative; Source = $file.FullName; Target = $target;
        SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash; Existed = (Test-Path -LiteralPath $target -PathType Leaf);
        PreviousSHA256 = $(if (Test-Path -LiteralPath $target -PathType Leaf) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash } else { $null }) }
}

$backupRoot = Join-Path $projectRoot ('artifacts\release-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$backupAncestor = $backupRoot
while ($backupAncestor.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $backupAncestor) -and ((Get-Item -LiteralPath $backupAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Backup path traverses a junction or symbolic link: $backupAncestor"
    }
    $backupAncestor = [IO.Path]::GetDirectoryName($backupAncestor)
}
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($entry in $entries | Where-Object Existed) {
    $backup = Join-Path $backupRoot $entry.Relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force | Out-Null
    Copy-Item -LiteralPath $entry.Target -Destination $backup
    if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $entry.PreviousSHA256) { throw "Backup verification failed: $($entry.Relative)" }
}
$entries | Select-Object Relative, SHA256, PreviousSHA256, Existed | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath (Join-Path $backupRoot 'update-manifest.json') -Encoding utf8

$written = [Collections.Generic.List[object]]::new()
try {
    foreach ($entry in $entries) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($entry.Target)) -Force | Out-Null
        $written.Add($entry) # include a partially failed copy in rollback
        Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.SHA256) { throw "Copy verification failed: $($entry.Relative)" }
    }
} catch {
    $failure = $_
    $rollbackFailures = [Collections.Generic.List[string]]::new()
    foreach ($entry in $written) {
        try {
            if ($entry.Existed) {
                if ((Test-Path -LiteralPath $entry.Target -PathType Leaf) -and
                    (Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -eq $entry.PreviousSHA256) { continue }
                Copy-Item -LiteralPath (Join-Path $backupRoot $entry.Relative) -Destination $entry.Target -Force
                if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.PreviousSHA256) { throw 'Restored file hash differs.' }
            } elseif (Test-Path -LiteralPath $entry.Target -PathType Leaf) {
                # Only remove a new program file attempted by this transaction.
                # No directory deletion and no user-data path can enter this list.
                Remove-Item -LiteralPath $entry.Target -Force
            }
        } catch { $rollbackFailures.Add($entry.Relative + ': ' + $_.Exception.Message) }
    }
    if ($rollbackFailures.Count) { throw "Update and rollback failed. Keep the app closed; restore program files from $backupRoot. $failure Rollback: $($rollbackFailures -join ' | ')" }
    throw "Update failed; overwritten program files restored from $backupRoot. $failure"
}
[pscustomobject]@{ InstalledDirectory = $publishRoot; BackupDirectory = $backupRoot; VerifiedProgramFiles = $entries.Count; PreservedDataDirectory = (Join-Path $publishRoot 'WorkspaceData') }
} finally {
    $updateLease.ReleaseMutex()
    $updateLease.Dispose()
}
