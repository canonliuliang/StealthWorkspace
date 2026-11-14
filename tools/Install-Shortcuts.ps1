param([string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
if (!$PublishDirectory) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'StealthWorkspace.exe')) { $PublishDirectory = $PSScriptRoot }
    else {
        $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $PublishDirectory = Join-Path $PSScriptRoot "..\bin\Release\net8.0-windows10.0.26100.0\win-$architecture\publish"
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
foreach ($folder in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'))) {
    $linkPath = Join-Path $folder 'Stealth 私密浏览器.lnk'
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $executable
    $link.WorkingDirectory = $publishRoot
    $link.IconLocation = $shortcutIcon + ',0'
    $link.Description = '打开 Stealth 私密浏览器与加密媒体工作台'
    $link.WindowStyle = 1
    $link.Save()
    if ($shell.CreateShortcut($linkPath).TargetPath -ne $executable) { throw "Shortcut verification failed: $linkPath" }
    Write-Output $linkPath
}

# The old batch launcher looks like a blank document and opens a console. Keep a
# familiar, icon-bearing entry that launches the app directly instead.
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$quickLinkPath = Join-Path $desktop '打开私密浏览器.lnk'
$quickLink = $shell.CreateShortcut($quickLinkPath)
$quickLink.TargetPath = $executable
$quickLink.WorkingDirectory = $publishRoot
$quickLink.IconLocation = $shortcutIcon + ',0'
$quickLink.Description = '直接打开 Stealth 私密浏览器'
$quickLink.WindowStyle = 1
$quickLink.Save()
Write-Output $quickLinkPath

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
foreach ($path in @((Join-Path $desktop 'Stealth 私密浏览器.lnk'), $quickLinkPath,
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Stealth 私密浏览器.lnk'))) {
    [StealthShortcutNotify]::SHChangeNotify(0x2000, 0x1005, $path, [IntPtr]::Zero)
}
