param([string]$BackupDirectory, [string]$Destination)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parent = Split-Path $repoRoot -Parent
if (!$BackupDirectory) { $BackupDirectory = Join-Path $parent 'StealthWorkspace-Backups/pre-overhaul-20261114' }
$backup = (Resolve-Path -LiteralPath $BackupDirectory).Path
$manifestPath = Join-Path $backup 'manifest-sha256.csv'
if (!(Test-Path -LiteralPath (Join-Path $backup 'VERIFIED.txt')) -or !(Test-Path -LiteralPath $manifestPath)) {
    throw 'The verified backup and hash manifest are required.'
}
if (!$Destination) { $Destination = Join-Path $parent ('StealthWorkspace-Restored-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw 'Choose a new destination. Existing folders are never overwritten.' }
New-Item -ItemType Directory -Path $target | Out-Null
& robocopy (Join-Path $backup 'project') $target /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /NFL /NDL /NP
if ($LASTEXITCODE -ge 8) { throw "Restore copy failed: $LASTEXITCODE" }
$count = 0
foreach ($entry in (Import-Csv -LiteralPath $manifestPath)) {
    $restored = [IO.Path]::GetFullPath((Join-Path $target $entry.Path))
    if (!$restored.StartsWith($target.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
    if (!(Test-Path -LiteralPath $restored -PathType Leaf) -or (Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -ne $entry.SHA256) {
        throw "Restored file failed verification: $($entry.Path)"
    }
    $count++
}
Write-Output "Restored and SHA-256 verified $count files: $target"
Write-Output 'Current development files and live user data were not modified.'
