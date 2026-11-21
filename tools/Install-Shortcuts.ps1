param([string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
if (!$PublishDirectory) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'StealthWorkspace.exe')) { $PublishDirectory = $PSScriptRoot }
    else {
        $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $PublishDirectory = Join-Path $PSScriptRoot "..\bin\Release\net10.0-windows10.0.26100.0\win-$architecture\publish"
    }
}
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$executable = Join-Path $publishRoot 'StealthWorkspace.exe'
$icon = Join-Path $publishRoot 'Assets\AppIcon.ico'
if (!(Test-Path -LiteralPath $executable) -or !(Test-Path -LiteralPath $icon)) { throw 'Publish the app and its icon before installing shortcuts.' }
# Keep the icon independent of the build tree. On the current Windows 11 ARM64
# host, the Shell icon handler returns E_FAIL for the .ico in the non-ASCII
# project path, despite ExtractIconEx decoding that file successfully. The
# per-user local icon copy is resolved correctly by the same handler.
# A content-addressed filename also avoids reusing the previous icon cache.
$sourceIconHash = (Get-FileHash -LiteralPath $icon -Algorithm SHA256).Hash
$iconHash = $sourceIconHash.Substring(0,12).ToLowerInvariant()
$iconRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StealthWorkspace\Icons'
New-Item -ItemType Directory -Path $iconRoot -Force | Out-Null
$shortcutIcon = Join-Path $iconRoot ('AppIcon-' + $iconHash + '.ico')
if (!(Test-Path -LiteralPath $shortcutIcon) -or
    (Get-FileHash -LiteralPath $shortcutIcon -Algorithm SHA256).Hash -ne $sourceIconHash) {
    Copy-Item -LiteralPath $icon -Destination $shortcutIcon -Force
}
if ((Get-FileHash -LiteralPath $shortcutIcon -Algorithm SHA256).Hash -ne $sourceIconHash) {
    throw 'Shortcut icon copy hash does not match the published icon.'
}
$shell = New-Object -ComObject WScript.Shell
# Construct Chinese labels from code points so Windows PowerShell 5.1 parses identically
# regardless of file BOM or active ANSI code page (CP936 / CP1252 / UTF-8).
$brandLabel = 'Stealth ' + (-join [char[]]@(0x79C1, 0x5BC6, 0x6D4F, 0x89C8, 0x5668))
$mainShortcutName = $brandLabel + '.lnk'
$mainShortcutDesc = (-join [char[]]@(0x6253, 0x5F00, 0x0020)) + $brandLabel + (-join [char[]]@(0x4E0E, 0x52A0, 0x5BC6, 0x5A92, 0x4F53, 0x5DE5, 0x4F5C, 0x53F0))
$quickShortcutName = (-join [char[]]@(0x6253, 0x5F00, 0x79C1, 0x5BC6, 0x6D4F, 0x89C8, 0x5668)) + '.lnk'
$quickShortcutDesc = (-join [char[]]@(0x76F4, 0x63A5, 0x6253, 0x5F00, 0x0020)) + $brandLabel

$shortcutChanges = @{}
function Save-AppLink([string]$linkPath, [string]$description) {
    $shortcutChanges[$linkPath] = if (Test-Path -LiteralPath $linkPath) { 0x2000 } else { 0x0002 }
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $executable
    $link.WorkingDirectory = $publishRoot
    $link.Arguments = ''
    $link.IconLocation = $shortcutIcon + ',0'
    $link.Description = $description
    $link.WindowStyle = 1
    $link.Save()
    $attributes = [IO.File]::GetAttributes($linkPath)
    if ($attributes -band [IO.FileAttributes]::Hidden) {
        [IO.File]::SetAttributes($linkPath, ($attributes -band (-bnot [IO.FileAttributes]::Hidden)))
    }
    $saved = $shell.CreateShortcut($linkPath)
    if ($saved.TargetPath -ne $executable -or $saved.WorkingDirectory -ne $publishRoot -or $saved.Arguments -or
        ($saved.IconLocation -replace ',\s*0$','') -ne $shortcutIcon) { throw "Shortcut verification failed: $linkPath" }
    Write-Output $linkPath
}
foreach ($folder in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'))) {
    Save-AppLink (Join-Path $folder $mainShortcutName) $mainShortcutDesc
}

# The old batch launcher looks like a blank document and opens a console. Keep a
# familiar, icon-bearing entry that launches the app directly instead.
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$quickLinkPath = Join-Path $desktop $quickShortcutName
Save-AppLink $quickLinkPath $quickShortcutDesc

# Notify only these shortcuts; do not delete the global icon cache or restart Explorer.
if (-not ('StealthShortcutNotify' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StealthShortcutNotify {
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    public static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr unused);
}
'@
}
foreach ($path in $shortcutChanges.Keys) {
    [StealthShortcutNotify]::SHChangeNotify($shortcutChanges[$path], 0x1005, $path, [IntPtr]::Zero)
}
