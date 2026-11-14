using System.Globalization;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckNativeZoomAuditAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        await NavigateAuditAsync(fixture.Address + "/page");
        MainWindow.Current?.BringToForeground();
        for (int i = 0; i < 20 && _browserControl?.IsLoaded != true; i++) await Task.Delay(25);
        await Task.Delay(150);
        SmokeTest.Report($"zoom audit: waiting for real foreground activation of test process {Environment.ProcessId}");
        for (int i = 0; i < 200 && !BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle); i++) await Task.Delay(100);
        async Task<double> RatioAsync() => double.Parse(await EvaluateAuditAsync("window.devicePixelRatio"), CultureInfo.InvariantCulture);
        if (!await SendNativeZoomAsync(0x30)) throw new InvalidOperationException("Native zoom shortcut was not sent");
        double baseline = await RatioAsync();
        await SendNativeZoomAsync(0xBB);
        double enlarged = await RatioAsync();
        bool native = enlarged > baseline * 1.05 && await EvaluateAuditAsync("document.documentElement.style.zoom==='' ") == "true";
        SmokeTest.Report($"zoom audit: real browser zoom without CSS mutation={native}; native ratio {baseline:F2}->{enlarged:F2}");
        if (!native) throw new InvalidOperationException("Native zoom did not change");
        await SendNativeZoomAsync(0x30);
        bool reset = Math.Abs(await RatioAsync() - baseline) < .01;
        SmokeTest.Report("zoom audit: reset returns to real 100 percent=" + reset);
        if (!reset) throw new InvalidOperationException("Native zoom reset failed");
        await SendNativeZoomAsync(0xBB);
        await NavigateAuditAsync(fixture.Address + "/page?next");
        // WebView2 documents user zoom as page-scoped. A navigation restores
        // its default, rather than applying a second CSS transform to the page.
        bool navigationDefault = Math.Abs(await RatioAsync() - baseline) < .01;
        SmokeTest.Report("zoom audit: navigation follows engine default without leftover CSS=" + navigationDefault);
        if (!navigationDefault) throw new InvalidOperationException("Native navigation zoom inconsistent");
        HideZoomPanel();
    }
}
