using System.Text.Json;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckNetworkPrivacyBoundaryAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        await NavigateAuditAsync(fixture.Address + "/page");
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
