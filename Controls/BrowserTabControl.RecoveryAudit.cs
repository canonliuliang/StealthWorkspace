using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckBrowserProcessRecoveryAuditAsync()
    {
        if (!SmokeTest.UiSmoke) throw new InvalidOperationException("Crash injection requires isolated UI diagnostics");
        await using var fixture = new BrowserHttpFixture();
        await NavigateAuditAsync(fixture.Address + "/page");
        var core = _hookedCore!;
        string? sessionDirectory = BrowserProfileManager.ActiveSessionDirectory;
        uint processId = core.BrowserProcessId;
        core.CookieManager.AddOrUpdateCookie(core.CookieManager.CreateCookie("browser-recovery", "keep", "127.0.0.1", "/"));
        try { await core.CallDevToolsProtocolMethodAsync("Browser.crash", "{}").AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { SmokeTest.Report("browser-process crash injection ended=" + ex.GetType().Name); }
        for (int i = 0; i < 200 && !_needsRecreate; i++) await Task.Delay(50);
        if (!_needsRecreate || _isInitialized) throw new InvalidOperationException("Browser exit not detected");
        await NavigateAuditAsync(fixture.Address + "/page?recovered");
        bool recovered = _isInitialized && !ReferenceEquals(core, _hookedCore) && !BrowserErrorBar.IsOpen;
        bool cookieRetained = (await _hookedCore!.CookieManager.GetCookiesAsync(fixture.Address)).Any(cookie => cookie.Name == "browser-recovery");
        bool sameProfile = sessionDirectory == BrowserProfileManager.ActiveSessionDirectory;
        bool newProcess = processId != _hookedCore!.BrowserProcessId;
        // Session cookies live in the browser process and are not guaranteed to
        // survive its crash. Do not retain a second host-side copy of login data.
        SmokeTest.Report($"recovery audit: browser process recreated={recovered}; new process={newProcess}; same profile directory={sameProfile}; session cookie retained (not guaranteed)={cookieRetained}");
        if (!recovered || !sameProfile || !newProcess) throw new InvalidOperationException("Browser process did not recover in its existing profile");
    }

    internal async Task CheckRendererDownloadRecoveryAuditAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        await NavigateAuditAsync(fixture.Address + "/page");
        var operation = await BeginDownloadAuditAsync(fixture.Address + "/download-slow");
        var record = BrowserDownloads.Items.Single(item => item.FilePath == operation.ResultFilePath);
        operation.Pause();
        for (int i = 0; i < 100 && !record.CanResume; i++) await Task.Delay(25);
        if (!record.CanResume || !record.Status.StartsWith("已暂停")) throw new InvalidOperationException("Paused download not represented correctly");
        bool recovered = await CheckSmokeCrashRecoveryAsync();
        bool retained = AuditTracksDownload(operation) && record.CanResume;
        record.TogglePauseResume();
        for (int i = 0; i < 200 && record.IsPending; i++) await Task.Delay(50);
        byte[] bytes = await File.ReadAllBytesAsync(record.FilePath);
        bool exact = record.IsCompleted && bytes.Length == 4194304 && bytes.All(value => value == 0x5A);
        SmokeTest.Report($"recovery audit: renderer reload succeeds={recovered}; paused download retained={retained}; resumed exact content={exact}");
        if (!recovered || !retained || !exact) throw new InvalidOperationException("Renderer recovery interrupted a browser-owned download");
    }

    internal async Task CheckBusyRendererAuditAsync()
    {
        if (!SmokeTest.UiSmoke) throw new InvalidOperationException("Busy renderer fixture requires isolated diagnostics");
        await using var fixture = new BrowserHttpFixture();
        await NavigateAuditAsync(fixture.Address + "/page");
        var core = _hookedCore!;
        await core.ExecuteScriptAsync("document.body.innerHTML='<input id=busyDraft value=keep>';setTimeout(()=>{const end=performance.now()+1500;while(performance.now()<end){}},0)");
        await Task.Delay(100);
        // Exercise the same recovery path without requiring an OS watchdog
        // timeout. Native watchdog notification itself is not simulated here.
        OnRendererUnresponsive(core);
        bool keptAlive = _isInitialized && !_needsRecreate && BrowserErrorBar.IsOpen;
        for (int i = 0; i < 100 && _responseProbe != null; i++) await Task.Delay(50);
        bool recovered = !BrowserErrorBar.IsOpen && ReferenceEquals(core, _hookedCore) &&
            await EvaluateAuditAsync("document.getElementById('busyDraft').value==='keep'") == "true";
        SmokeTest.Report($"recovery audit: synthetic busy notification preserves controller={keptAlive}; script finishes and draft survives={recovered}");
        if (!keptAlive || !recovered) throw new InvalidOperationException("Busy renderer recovery discarded state");
    }
}
