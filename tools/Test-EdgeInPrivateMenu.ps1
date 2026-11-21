param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Low
)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath (Join-Path $AppDirectory 'StealthWorkspace.exe')).Path
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $out) { throw 'Choose a new output directory for this isolated UI test.' }
[void][IO.Directory]::CreateDirectory($out)
$key = 'edge-check-' + [guid]::NewGuid().ToString('N')
$data = Join-Path $out $key
[void][IO.Directory]::CreateDirectory($data)
$report = Join-Path $out 'result.txt'
$profile = Join-Path $data 'isolated-edge-profile'
$arguments = @('--ui-smoke', $report, '--data-root', $data, '--browser-audit', '--menu-actions-only', '--verify-edge-inprivate')
$start = [Diagnostics.ProcessStartInfo]::new($app)
$start.UseShellExecute = $false
$start.WorkingDirectory = Split-Path $app
if ($Low) {
    # Run a private copy of Node with a lower integrity label, then inherit
    # that token into the ordinary, unmodified application executable.
    & icacls.exe $out /setintegritylevel '(OI)(CI)L' /T | Out-Null
    if ($LASTEXITCODE) { throw 'Could not prepare low-integrity synthetic data.' }
    $runner = Join-Path $out 'test-runner.exe'
    Copy-Item -LiteralPath (Get-Command node.exe).Source -Destination $runner
    & icacls.exe $runner /setintegritylevel L | Out-Null
    if ($LASTEXITCODE) { throw 'Could not lower the test launcher integrity.' }
    $start.FileName = $runner
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add('-e')
    $start.ArgumentList.Add('const p=require("node:child_process").spawn(process.argv[1],process.argv.slice(2),{stdio:"inherit"});p.on("exit",code=>process.exit(code??1));p.on("error",()=>process.exit(1));')
    $start.ArgumentList.Add('--')
    $start.ArgumentList.Add($app)
}
foreach ($arg in $arguments) { $start.ArgumentList.Add($arg) }
Add-Type -AssemblyName UIAutomationTypes,UIAutomationClient
if (-not ('EdgeAcceptanceToken' -as [type])) {
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
public class EdgeAcceptanceToken {
    [DllImport("advapi32",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int type,IntPtr buffer,int size,out int returned);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32")] static extern IntPtr GetSidSubAuthority(IntPtr sid,uint index);
    public static int Read(IntPtr process) {
        if(!OpenProcessToken(process,8,out var token)) throw new Win32Exception();
        try {
            GetTokenInformation(token,25,IntPtr.Zero,0,out int size);
            var buffer=Marshal.AllocHGlobal(size);
            try {
                if(!GetTokenInformation(token,25,buffer,size,out size)) throw new Win32Exception();
                var sid=Marshal.ReadIntPtr(buffer);
                return Marshal.ReadInt32(GetSidSubAuthority(sid,(uint)(Marshal.ReadByte(GetSidSubAuthorityCount(sid))-1)));
            } finally { Marshal.FreeHGlobal(buffer); }
        } finally { CloseHandle(token); }
    }
}
'@
}
function Find-TestEdge {
    # Limit both the process query and the UI tree to the unique test profile.
    $matches = Get-CimInstance -Query "SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='msedge.exe' AND CommandLine LIKE '%$key%'"
    foreach ($match in $matches) {
        if ($match.CommandLine -match '(?:^|\s)--type=' -or
            !$match.CommandLine.Replace('"','').Contains('--user-data-dir=' + $profile)) { continue }
        $browser = Get-Process -Id $match.ProcessId -ErrorAction SilentlyContinue
        if ($browser -and $browser.MainWindowHandle -ne 0) { return $browser }
    }
}
function Read-Window($browser) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($browser.MainWindowHandle)
    $type = [Windows.Automation.AutomationElement]::ControlTypeProperty
    $conditions = [Windows.Automation.Condition[]]@(
        [Windows.Automation.PropertyCondition]::new($type,[Windows.Automation.ControlType]::Edit),
        [Windows.Automation.PropertyCondition]::new($type,[Windows.Automation.ControlType]::Button),
        [Windows.Automation.PropertyCondition]::new($type,[Windows.Automation.ControlType]::TabItem))
    $nodes = $root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.OrCondition]::new($conditions))
    $badge = $null; $address = $null; $tabs = @()
    foreach ($node in $nodes) {
        $state = $node.Current
        if ($state.ControlType -eq [Windows.Automation.ControlType]::Button -and $state.Name -match 'InPrivate' -and !$state.IsOffscreen) { $badge = $state.Name }
        if ($state.ControlType -eq [Windows.Automation.ControlType]::TabItem) { $tabs += $state.Name }
        if ($state.ControlType -eq [Windows.Automation.ControlType]::Edit -and
            ($state.Name -match 'address|地址' -or $state.AutomationId -match 'address|omnibox')) {
            $pattern = $null
            if ($node.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)) { $address = $pattern.Current.Value }
        }
    }
    $body = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'200 OK'))
    [pscustomobject]@{ title=$root.Current.Name; badge=$badge; address=$address; tabs=$tabs; fixtureBody=($body -and !$body.Current.IsOffscreen);
        pid=$browser.Id; handle=$browser.MainWindowHandle.ToInt64() }
}
$child = [Diagnostics.Process]::Start($start)
$testApp = $null
try {
    foreach ($kind in @('blank','page')) {
        $mark = Join-Path $data ('edge-window-' + $kind)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath ($mark+'.request.json')) -and !$child.HasExited -and $timer.Elapsed.TotalSeconds -lt 75) {
            if ((Test-Path -LiteralPath $report) -and (Get-Content $report -Tail 1) -match 'waiting for test-window activation, pid=(\d+)') {
                $pending = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
                if ($pending -and $pending.Path -eq $app) { & (Join-Path $PSScriptRoot 'Set-UiTestForeground.ps1') -TestProcess $pending | Out-Null }
            }
            Start-Sleep -Milliseconds 100
        }
        if (!(Test-Path -LiteralPath ($mark+'.request.json'))) { throw "Application did not reach $kind. Inspect $report" }
        $request = Get-Content -LiteralPath ($mark+'.request.json') -Raw | ConvertFrom-Json
        $testApp = Get-Process -Id $request.processId
        if ($testApp.Path -ne $app) { throw 'Request not from the synthetic application.' }
        $rid = [EdgeAcceptanceToken]::Read($testApp.Handle)
        if ($rid -ne $(if ($Low) {4096} else {8192})) { throw "Unexpected application integrity RID $rid" }
        if ($Low) {
            if (Find-TestEdge) { throw 'Restricted guidance unexpectedly started an external Edge.' }
            continue
        }
        $timer.Restart()
        $verified = $false
        while (!$child.HasExited -and $timer.Elapsed.TotalSeconds -lt 40) {
            $browser = Find-TestEdge
            if ($browser) {
                try { $state = Read-Window $browser } catch [Windows.Automation.ElementNotAvailableException] { continue }
                $private = $state.badge -or $state.title -match 'InPrivate'
                if ($kind -eq 'blank') {
                    $correct = $null -ne $state.address -and $state.address -match '^(?:|edge://(?:newtab|new-tab-page|inprivate)/?|about:blank)$' -and
                        $state.tabs.Count -gt 0 -and !($state.tabs -match '已崩溃|crashed')
                } else {
                    $correct = ($state.address -eq $request.expectedPage -or ('http://'+$state.address) -eq $request.expectedPage) -and
                        ($state.tabs -match 'HTTP fixture' -or $state.title -match 'HTTP fixture') -and $state.fixtureBody
                }
                if ($private -and $correct) { $verified=$true; break }
            }
            Start-Sleep -Milliseconds 150
        }
        if (!$verified) { throw "The isolated InPrivate $kind window did not show the expected content." }
        $state | Add-Member -NotePropertyName appIntegrityRid -NotePropertyValue $rid
        $state | Add-Member -NotePropertyName expectedPage -NotePropertyValue $request.expectedPage
        $state | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out ($kind+'-window.json'))
        if (Get-Command winapp -ErrorAction SilentlyContinue) {
            Start-Sleep -Milliseconds 550
            & winapp ui screenshot --window $browser.MainWindowHandle.ToInt64() --focus --output (Join-Path $out ($kind+'-inprivate.png')) | Out-Null
        }
        if (!$browser.CloseMainWindow()) { throw 'The isolated Edge window did not accept close.' }
        for ($i=0; $i -lt 40 -and (Find-TestEdge); $i++) { Start-Sleep -Milliseconds 80 }
        if (Find-TestEdge) { throw 'The isolated Edge window did not close.' }
        & (Join-Path $PSScriptRoot 'Set-UiTestForeground.ps1') -TestProcess $testApp | Out-Null
        [IO.File]::WriteAllText($mark+'.tmp',$request.nonce)
        [IO.File]::Move($mark+'.tmp',$mark+'.reply')
    }
    if ($Low) {
        for ($i=0; $i -lt 150 -and !$child.HasExited; $i++) {
            if ((Get-Content $report -Raw).Contains('confirmed the exact URL without clipboard history or roaming')) { break }
            Start-Sleep -Milliseconds 100
        }
        $lines=Get-Content $report -Raw
        if ((Find-TestEdge) -or !$lines.Contains('close copied nothing') -or !$lines.Contains('confirmed the exact URL without clipboard history or roaming')) {
            throw 'Restricted handoff did not satisfy the clipboard, confirmation and launch checks.'
        }
    }
    [pscustomobject]@{ appId=$testApp.Id; appIntegrityRid=$rid; verifiedMode=$(if ($Low) {'manual InPrivate guidance'} else {'external InPrivate browser'}); passed=$true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'edge-handoff.json')
    if (!$child.WaitForExit(90000) -or $child.ExitCode -ne 0) { throw 'The synthetic application did not exit successfully.' }
    if ((Get-Content $report -Raw) -notmatch 'AUDIT RESULT: OK') { throw "The complete menu workflow failed. Inspect $report" }
    Write-Output "Verified both menu entries with app RID $rid. Reports: $out"
} finally {
    $remaining = Find-TestEdge
    if ($remaining) { $null=$remaining.CloseMainWindow() }
    if ($testApp -and !$testApp.HasExited) { $null=$testApp.CloseMainWindow() }
}
