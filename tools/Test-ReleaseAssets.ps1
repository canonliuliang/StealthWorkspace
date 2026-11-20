param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$')][string]$Version,
    [string]$ReferenceDirectory,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit
)
$ErrorActionPreference = 'Stop'

function Test-PackageBinary($entry, [string]$machine, [switch]$CheckAssembly) {
    $inputStream = $entry.Open()
    $image = [IO.MemoryStream]::new()
    try {
        $inputStream.CopyTo($image)
        $image.Position = 0
        $pe = [Reflection.PortableExecutable.PEReader]::new($image)
        try {
            $headers = $pe.PEHeaders
            if ($headers.CoffHeader.Machine.ToString() -cne $machine -or $headers.PEHeader.Magic.ToString() -cne 'PE32Plus') {
                throw "$($entry.FullName) is not a 64-bit $machine Windows image"
            }
            if (!$CheckAssembly) { return }
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $assembly = $metadata.GetAssemblyDefinition()
            if ($metadata.GetString($assembly.Name) -cne 'StealthWorkspace') { throw 'Incorrect managed application assembly' }
            foreach ($handle in $assembly.GetCustomAttributes()) {
                $attribute = $metadata.GetCustomAttribute($handle)
                if ($attribute.Constructor.Kind -ne 'MemberReference') { continue }
                $constructor = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
                if ($constructor.Parent.Kind -ne 'TypeReference') { continue }
                $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
                if ($metadata.GetString($type.Namespace) -cne 'System.Reflection' -or
                    $metadata.GetString($type.Name) -cne 'AssemblyInformationalVersionAttribute') { continue }
                $value = $metadata.GetBlobReader($attribute.Value)
                if ($value.ReadUInt16() -ne 1) { throw 'Invalid assembly version attribute' }
                $actual = $value.ReadSerializedString()
                if ($actual.Split('+')[0] -cne $Version -or ($ExpectedCommit -and $actual -ine "$Version+$ExpectedCommit")) {
                    throw "Managed assembly identity '$actual' differs from the expected version or commit"
                }
                return
            }
            throw 'The managed application is missing its informational version'
        } finally { $pe.Dispose() }
    } finally { $image.Dispose(); $inputStream.Dispose() }
}

$root = (Resolve-Path -LiteralPath $Directory).Path
$manifest = @(Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt'))
if ($manifest.Count -ne 2) { throw 'The public checksum manifest must list exactly two archives.' }
foreach ($runtime in 'win-x64', 'win-arm64') {
    $name = "Stealth-$Version-$runtime.zip"
    $matchesForFile = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter $name)
    if ($matchesForFile.Count -ne 1) { throw "Expected exactly one $name" }
    $path = $matchesForFile[0].FullName
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest -cnotcontains "$hash  $name") { throw "Checksum or filename mismatch for $runtime" }
    if ($ReferenceDirectory) {
        $original = @(Get-ChildItem -LiteralPath $ReferenceDirectory -Recurse -File -Filter $name)
        if ($original.Count -ne 1 -or (Get-FileHash -LiteralPath $original[0].FullName).Hash -ne $hash) {
            throw "Downloaded asset differs from its tagged build for $runtime"
        }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            if (!$entryNames.Add($entry.FullName.Replace('\','/'))) { throw "Duplicate archive entry $($entry.FullName) in $runtime" }
        }
        foreach ($required in 'release.json', 'StealthWorkspace.exe', 'StealthWorkspace.dll', 'StealthWorkspace.pri', 'START-HERE.md', 'PRIVACY.md') {
            $entry = $zip.GetEntry($required)
            if (!$entry -or $entry.Length -le 0) { throw "$runtime missing or empty $required" }
        }
        $machine = if ($runtime -eq 'win-x64') { 'Amd64' } else { 'Arm64' }
        Test-PackageBinary $zip.GetEntry('StealthWorkspace.exe') $machine
        Test-PackageBinary $zip.GetEntry('StealthWorkspace.dll') $machine -CheckAssembly
        $reader = [IO.StreamReader]::new($zip.GetEntry('release.json').Open())
        try { $meta = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($meta.version -cne $Version -or $meta.runtime -cne $runtime -or $meta.channel -cne 'preview' -or $meta.signed -isnot [bool] -or $meta.signed) {
            throw "The archive does not match its release identity for $runtime"
        }
    }
    finally { $zip.Dispose() }
    Write-Output "$hash  $name"
}
