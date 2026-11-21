using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    // Explicit diagnostic mode on an isolated test vault. Never collect page content,
    // credentials, cookies, or browsing telemetry during normal use.
    internal async Task RunNetworkProbeAsync(string address)
    {
        var watch = Stopwatch.StartNew();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        WebView2? reference = null;
        CoreWebView2 core;
        if (SmokeTest.ReferenceBrowserProbe)
        {
            StartSurface.Visibility = Visibility.Collapsed;
            reference = new WebView2();
            BrowserHost.Children.Add(reference);
            var environment = await BrowserProfileManager.GetSharedEnvironmentAsync(_workspace);
            await reference.EnsureCoreWebView2Async(environment);
            core = reference.CoreWebView2;
            await BrowserProfileManager.PrepareForNavigationAsync(core, environment, _workspace);
        }
        else
        {
            await InitializeAsync(null);
            core = _hookedCore ?? throw new InvalidOperationException("Probe core unavailable");
        }
        SmokeTest.Report($"probe: reference={SmokeTest.ReferenceBrowserProbe}; kernel ready ms={watch.ElapsedMilliseconds}; profile on local appdata={BrowserProfileManager.ActiveSessionDirectory?.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)}");
        SmokeTest.Report($"probe: awaiting foreground, pid={Environment.ProcessId}");
        for (int i = 0; i < 300 && !BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle); i++) await Task.Delay(100);
        if (!BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)) throw new InvalidOperationException("Network probe needs a foreground window");
        var completion = new TaskCompletionSource<bool>();
        var responses = new Dictionary<string, int>();
        void Response(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            // Hosts and HTTP status only: no query strings, cookies or bodies.
            if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var request)) return;
            string key = request.Host + " " + args.Response.StatusCode;
            responses[key] = responses.GetValueOrDefault(key) + 1;
        }
        ulong? navigation = null;
        void Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!args.IsRedirected) navigation = args.NavigationId;
        }
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != navigation) return;
            SmokeTest.Report($"probe: navigation {watch.ElapsedMilliseconds}ms; success={args.IsSuccess}; HTTP={args.HttpStatusCode}; error={args.WebErrorStatus}");
            completion.TrySetResult(true);
        }
        void DomReady(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            if (args.NavigationId == navigation) SmokeTest.Report($"probe: DOM ready {watch.ElapsedMilliseconds}ms");
        }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        core.DOMContentLoaded += DomReady;
        core.WebResourceResponseReceived += Response;
        try
        {
            watch.Restart();
            if (reference != null) core.Navigate(uri.AbsoluteUri); else NavigateTo(uri);
            await Task.WhenAny(completion.Task, Task.Delay(20000));
            await Task.Delay(15000);
            string metrics = await core.ExecuteScriptAsync("JSON.stringify((()=>{const n=performance.getEntriesByType('navigation')[0];let canvas;try{document.createElement('canvas').getContext('2d').getImageData(0,0,1,1);canvas=true}catch{canvas=false}return {origin:location.origin,title:document.title,ready:document.readyState,canvasReadable:canvas,resources:performance.getEntriesByType('resource').length,navigation:n?{dns:n.domainLookupEnd-n.domainLookupStart,connect:n.connectEnd-n.connectStart,ttfb:n.responseStart-n.requestStart,dom:n.domContentLoadedEventEnd,load:n.loadEventEnd}:null}})())").AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            SmokeTest.Report("probe: " + metrics);
            SmokeTest.Report("probe response counts: " + System.Text.Json.JsonSerializer.Serialize(responses));
            SmokeTest.Report("probe environment: " + await core.ExecuteScriptAsync("JSON.stringify({ua:navigator.userAgent,cookies:navigator.cookieEnabled,secure:isSecureContext,webdriver:navigator.webdriver})"));
            SmokeTest.Report("probe visibility and slow resource hosts: " + await core.ExecuteScriptAsync("JSON.stringify({visibility:document.visibilityState,focus:document.hasFocus(),slow:performance.getEntriesByType('resource').sort((a,b)=>b.duration-a.duration).slice(0,8).map(e=>({host:(()=>{try{return new URL(e.name).host}catch{return ''}})(),type:e.initiatorType,ms:Math.round(e.duration),protocol:e.nextHopProtocol}))})"));
        }
        catch (Exception ex) { SmokeTest.Report("probe: diagnostic " + ex.GetType().Name); }
        finally
        {
            core.NavigationStarting -= Starting; core.NavigationCompleted -= Completed; core.DOMContentLoaded -= DomReady;
            core.WebResourceResponseReceived -= Response;
            if (reference != null) { reference.Close(); BrowserHost.Children.Remove(reference); }
        }
    }
}
