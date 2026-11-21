using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckNetworkPrivacyBoundaryAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        await NavigateAuditAsync(fixture.Address + "/page");
        var core = _hookedCore ?? throw new InvalidOperationException("Permission fixture has no browser");
        foreach (var kind in new[] { CoreWebView2PermissionKind.FileReadWrite, CoreWebView2PermissionKind.MultipleAutomaticDownloads })
        {
            if (!_tabPermissions.Blocks(kind, core.Source, SettingsService.Instance.Data) ||
                await ConfirmFilePermissionAsync(core, kind, "https://other.example/"))
                throw new InvalidOperationException("A file permission escaped its one-time confirmation or origin");
            var decision = ConfirmFilePermissionAsync(core, kind, core.Source);
            Microsoft.UI.Xaml.Controls.Button? allow = null;
            for (int i = 0; i < 80 && allow == null && !decision.IsCompleted; i++)
            {
                await Task.Delay(25);
                allow = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
                    .Select(popup => ImageExperienceAcceptance.FindConfirm(popup.Child, "允许本次请求")).FirstOrDefault(button => button != null);
            }
            if (allow == null || decision.IsCompleted) throw new InvalidOperationException("File permission did not wait for explicit consent");
            MediaAcceptance.Invoke(allow);
            if (!await decision) throw new InvalidOperationException("Current document did not receive its one-time permission");
        }
        SmokeTest.Report("privacy: same-origin file writes and multiple downloads waited for individual native confirmations; another origin was denied");
        await EvaluateAuditAsync("""
            (() => {
              window.geoResult = 'pending';
              navigator.geolocation.getCurrentPosition(
                () => { window.geoResult = 'allowed'; },
                e => { window.geoResult = e.code === 1 ? 'denied' : 'unavailable'; },
                { timeout: 5000 });
              window.iceCounts = { mdns: 0, literal: 0, other: 0 };
              window.iceDone = false;
              (async () => {
                const pc = new RTCPeerConnection({ iceServers: [] });
                const stop = () => { if (!window.iceDone) { window.iceDone = true; pc.close(); } };
                pc.onicecandidate = e => {
                  if (!e.candidate) { stop(); return; }
                  const address = e.candidate.address || '';
                  if (address.toLowerCase().endsWith('.local')) window.iceCounts.mdns++;
                  else if (address.includes(':') || /^\d+\.\d+\.\d+\.\d+$/.test(address)) window.iceCounts.literal++;
                  else window.iceCounts.other++;
                };
                pc.createDataChannel('local-test');
                await pc.setLocalDescription(await pc.createOffer());
                setTimeout(stop, 6000);
              })().catch(() => { window.iceDone = true; window.iceFailed = true; });
            })();
            """);
        for (int i = 0; i < 80; i++)
        {
            if (await EvaluateAuditAsync("window.iceDone && window.geoResult !== 'pending'") == "true") break;
            await Task.Delay(100);
        }
        if (await EvaluateAuditAsync("window.geoResult === 'denied'") != "true")
            throw new InvalidOperationException("Default geolocation request did not return permission denied");
        if (await EvaluateAuditAsync("window.iceDone && !window.iceFailed") != "true")
            throw new InvalidOperationException("Local WebRTC observation could not complete");
        // Only counts leave the fixture. No candidate address or location enters a report.
        using var counts = JsonDocument.Parse(await EvaluateAuditAsync("window.iceCounts"));
        int Count(string name) => counts.RootElement.GetProperty(name).GetInt32();
        SmokeTest.Report($"privacy: page geolocation denied; WebRTC without STUN/TURN, mDNS={Count("mdns")}, literal={Count("literal")}, other={Count("other")}");
    }
}
