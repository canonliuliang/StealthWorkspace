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
        await RequireKeyboardForegroundAsync("zoom");
        async Task<double> RatioAsync() => double.Parse(await EvaluateAuditAsync("window.devicePixelRatio"), CultureInfo.InvariantCulture);
        if (!await SendNativeZoomAsync(0x30)) throw new InvalidOperationException("Native zoom shortcut was not sent");
        double baseline = await RatioAsync();
        async Task<double> SendAndObserveAsync(ushort key, Func<double, bool> changed)
        {
            if (_browserControl?.Focus(Microsoft.UI.Xaml.FocusState.Keyboard) != true)
                throw new InvalidOperationException("WebView did not accept keyboard focus");
            for (int i = 0; i < 40 && await EvaluateAuditAsync("document.hasFocus()") != "true"; i++) await Task.Delay(25);
            if (await EvaluateAuditAsync("document.hasFocus()") != "true" || !BrowserZoomInput.Send(MainWindow.CurrentHandle, key))
                throw new InvalidOperationException("Keyboard shortcut did not reach the focused test window");
            double ratio = await RatioAsync();
            for (int i = 0; i < 60 && !changed(ratio); i++) { await Task.Delay(25); ratio = await RatioAsync(); }
            return ratio;
        }
        double enlarged = await SendAndObserveAsync(0xBB, ratio => ratio > baseline * 1.05);
        bool native = enlarged > baseline * 1.05 && await EvaluateAuditAsync("document.documentElement.style.zoom==='' ") == "true";
        SmokeTest.Report($"zoom audit: real browser zoom without CSS mutation={native}; native ratio {baseline:F2}->{enlarged:F2}");
        if (!native) throw new InvalidOperationException("Native zoom did not change");
        bool reset = Math.Abs(await SendAndObserveAsync(0x30, ratio => Math.Abs(ratio - baseline) < .01) - baseline) < .01;
        SmokeTest.Report("zoom audit: reset returns to real 100 percent=" + reset);
        if (!reset) throw new InvalidOperationException("Native zoom reset failed");
        if (await SendAndObserveAsync(0xBB, ratio => ratio > baseline * 1.05) <= baseline * 1.05)
            throw new InvalidOperationException("Keyboard zoom was not applied before navigation");
        await NavigateAuditAsync(fixture.Address + "/page?next");
        // WebView2 documents user zoom as page-scoped. A navigation restores
        // its default, rather than applying a second CSS transform to the page.
        bool navigationDefault = Math.Abs(await RatioAsync() - baseline) < .01;
        SmokeTest.Report("zoom audit: navigation follows engine default without leftover CSS=" + navigationDefault);
        if (!navigationDefault) throw new InvalidOperationException("Native navigation zoom inconsistent");
        HideZoomPanel();
    }

    private static async Task RequireKeyboardForegroundAsync(string operation)
    {
        if (BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)) return;
        SmokeTest.Report($"{operation} audit: waiting for real foreground activation of test process {Environment.ProcessId}");
        for (int i = 0; i < 200 && !BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle); i++) await Task.Delay(100);
        if (!BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle))
            throw new InvalidOperationException("Test window never became foreground for " + operation);
    }
}
