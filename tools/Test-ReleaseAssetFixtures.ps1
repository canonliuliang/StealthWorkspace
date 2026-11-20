param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Directory).Path
$verify = Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1'
& $verify -Directory $source -Version $Version -ExpectedCommit $ExpectedCommit

# Keep each negative case small. Use real PE images from the two verified ZIPs,
# and alter copies only. A recomputed checksum exercises the checks inside the ZIP.
$fixture = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('stealth-package-test-' + [guid]::NewGuid()))
$entries = 'release.json','StealthWorkspace.exe','StealthWorkspace.dll','StealthWorkspace.pri','START-HERE.md','PRIVACY.md'
function Copy-Entry($original, $archive, [string]$name) {
    $inputStream = $original.Open()
    $outputStream = $archive.CreateEntry($name).Open()
    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
}
function Update-Checksums($path) {
    Get-ChildItem -LiteralPath $path -Filter '*.zip' -File | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    } | Set-Content -LiteralPath (Join-Path $path 'SHA256SUMS.txt') -Encoding ascii
}
function Expect-Failure([string]$name, [string]$message, [scriptblock]$action) {
    try { & $action | Out-Null } catch {
        if (!$_.Exception.Message.Contains($message)) { throw }
        Write-Output "PASS $name"
        return
    }
    throw "Invalid package unexpectedly passed case '$name'."
}
try {
    $original = New-Item -ItemType Directory -Path (Join-Path $fixture.FullName 'original')
    foreach ($runtime in 'win-x64','win-arm64') {
        $name = "Stealth-$Version-$runtime.zip"
        $real = @(Get-ChildItem -LiteralPath $source -Recurse -File -Filter $name)
        $archive = [IO.Compression.ZipFile]::OpenRead($real[0].FullName)
        $small = [IO.Compression.ZipFile]::Open((Join-Path $original.FullName $name), 'Create')
        try {
            foreach ($entryName in $entries) { Copy-Entry $archive.GetEntry($entryName) $small $entryName }
        } finally { $small.Dispose(); $archive.Dispose() }
    }
    Update-Checksums $original.FullName
    foreach ($case in 'empty-exe','duplicate-readme','wrong-machine','wrong-commit') {
        $stage = New-Item -ItemType Directory -Path (Join-Path $fixture.FullName $case)
        Get-ChildItem -LiteralPath $original.FullName -File | Copy-Item -Destination $stage.FullName
        $x64 = [IO.Compression.ZipFile]::Open((Join-Path $stage.FullName "Stealth-$Version-win-x64.zip"), 'Update')
        $expected = $ExpectedCommit
        try {
            switch ($case) {
                'empty-exe' {
                    $x64.GetEntry('StealthWorkspace.exe').Delete()
                    $null = $x64.CreateEntry('StealthWorkspace.exe')
                    $message = 'missing or empty StealthWorkspace.exe'
                }
                'duplicate-readme' {
                    Copy-Entry $x64.GetEntry('PRIVACY.md') $x64 'privacy.md'
                    $message = 'Duplicate archive entry'
                }
                'wrong-machine' {
                    $arm = [IO.Compression.ZipFile]::OpenRead((Join-Path $stage.FullName "Stealth-$Version-win-arm64.zip"))
                    try {
                        $x64.GetEntry('StealthWorkspace.exe').Delete()
                        Copy-Entry $arm.GetEntry('StealthWorkspace.exe') $x64 'StealthWorkspace.exe'
                    } finally { $arm.Dispose() }
                    $message = 'is not a 64-bit Amd64'
                }
                'wrong-commit' {
                    $expected = if ($ExpectedCommit -ceq ('0' * 40)) { '1' * 40 } else { '0' * 40 }
                    $message = 'differs from the expected version or commit'
                }
            }
        } finally { $x64.Dispose() }
        Update-Checksums $stage.FullName
        Expect-Failure $case $message { & $verify -Directory $stage.FullName -Version $Version -ExpectedCommit $expected }
    }
} finally {
    $resolved = (Resolve-Path -LiteralPath $fixture.FullName).Path
    $parent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    if ((Split-Path $resolved -Parent) -eq $parent -and (Split-Path $resolved -Leaf) -match '^stealth-package-test-[0-9a-f-]{36}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
