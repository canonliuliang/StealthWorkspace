param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$Runtime,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$')][string]$Version,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "artifacts/releases/$Version/$Runtime" }
$releaseRoot = [IO.Path]::GetFullPath($OutputDirectory)
# Build into a new location. Never package or mirror a live installation.
if (Test-Path -LiteralPath $releaseRoot) { throw "Output directory already exists; choose a new output path: $releaseRoot" }
$appRoot = Join-Path $releaseRoot 'app'
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
& dotnet publish (Join-Path $repoRoot 'StealthWorkspace.csproj') -c Release -r $Runtime "-p:Platform=$platform" --self-contained true -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false -o $appRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $appRoot 'StealthWorkspace.dll')).ProductVersion.Split('+')[0]
$uiVersion = [regex]::Match([IO.File]::ReadAllText((Join-Path $repoRoot 'Services/AppInfo.cs')), 'DisplayVersion = "([^"]+)"').Groups[1].Value
if ($binaryVersion -ne $Version -or $uiVersion -ne $Version) { throw "Package, binary and UI versions must match ($Version, $binaryVersion, $uiVersion)." }
foreach ($required in 'StealthWorkspace.exe','StealthWorkspace.dll','StealthWorkspace.deps.json','StealthWorkspace.runtimeconfig.json','StealthWorkspace.pri','Assets/AppIcon.ico') {
    if (!(Test-Path -LiteralPath (Join-Path $appRoot $required) -PathType Leaf)) { throw "Missing runtime file: $required" }
}
$forbidden = Get-ChildItem -LiteralPath $appRoot -Recurse -Force | Where-Object {
    $_.Name -match '^(WorkspaceData|MediaVault|IsolatedDownloads.*|BrowserSessions|Stealth_WebProfile.*|StealthVault_Store|system_catalog\.dat.*|rotate\.dat.*|remove\.dat.*|settings\.dat.*|app\.log.*|crash\.log|\.migrated|\.legacy-import-disabled.*|\.legacy-migration-.*)$' -or $_.Extension -in '.svault','.svbackup','.swbackup','.pfx','.key','.dmp'
}
if ($forbidden) { throw 'Publish output contains private data or signing material; packaging stopped.' }
& (Join-Path $PSScriptRoot 'Test-Core.ps1') -PublishDirectory $appRoot -OutputPath (Join-Path $releaseRoot 'core-tests.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Shortcuts.ps1') -Destination $appRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/INSTALL.md') -Destination (Join-Path $appRoot 'START-HERE.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/PRIVACY.md') -Destination (Join-Path $appRoot 'PRIVACY.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'CHANGELOG.md') -Destination $appRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $appRoot
# Carry the actual license/notice files for resolved packages, including the
# self-contained .NET runtime, rather than only linking to mutable web pages.
$licenseRoot = Join-Path $appRoot 'licenses'
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
$assets = Get-Content (Join-Path $repoRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
$deps = Get-Content (Join-Path $appRoot 'StealthWorkspace.deps.json') -Raw | ConvertFrom-Json
$packageKeys = @($assets.libraries.PSObject.Properties.Name) + @($deps.libraries.PSObject.Properties.Name)
foreach ($key in ($packageKeys | Sort-Object -Unique)) {
    $packageKey = $key.ToLowerInvariant().Replace('runtimepack.','')
    if ($packageKey -notmatch '^[a-z0-9_.-]+/[a-z0-9_.+-]+$') { continue }
    foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
        $packageRoot = Join-Path $cache $packageKey
        if (!(Test-Path -LiteralPath $packageRoot -PathType Container)) { continue }
        $notices = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(license|notice|third.party.notices?)(\.|$)' })
        if ($notices.Count -eq 0) { continue }
        $destination = Join-Path $licenseRoot ($packageKey.Replace('/','-'))
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $destination }
        break
    }
}
if (!(Get-ChildItem -LiteralPath $licenseRoot -Directory | Where-Object Name -Like 'microsoft.netcore.app.runtime*')) { throw 'Self-contained .NET runtime license is missing.' }
[ordered]@{version=$Version;runtime=$Runtime;createdUtc=[DateTime]::UtcNow.ToString('o');signed=$false;channel='preview'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $appRoot 'release.json') -Encoding utf8
$zipPath = Join-Path $releaseRoot "Stealth-$Version-$Runtime.zip"
Compress-Archive -Path (Join-Path $appRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Package: $zipPath"
Write-Output "SHA256: $hash"
