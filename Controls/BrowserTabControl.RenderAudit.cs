using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    // Explicit developer diagnostic, using only generated local content. It never
    // records user pages or enables a remote-debugging endpoint in normal browsing.
    internal async Task RunRenderAuditAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        await InitializeAsync(null);
        var app = _hookedCore ?? throw new InvalidOperationException("Render audit core unavailable");
        SmokeTest.Report($"render audit: awaiting foreground pid={Environment.ProcessId}");
        for (int i = 0; i < 300 && !BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle); i++) await Task.Delay(100);
        if (!BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)) throw new InvalidOperationException("Render audit requires foreground");
        StartSurface.Visibility = Visibility.Collapsed;
        var plain = new WebView2();
        BrowserHost.Children.Add(plain);
        try
        {
            await plain.EnsureCoreWebView2Async(app.Environment);
            await NavigateRenderFixtureAsync(app, fixture.Address + "/reading");
            await NavigateRenderFixtureAsync(plain.CoreWebView2, fixture.Address + "/reading");
            string graphics = await app.ExecuteScriptAsync("JSON.stringify((()=>{const c=document.createElement('canvas'),g=c.getContext('webgl');if(!g)return {available:false};const d=g.getExtension('WEBGL_debug_renderer_info');const r={available:true,renderer:d?g.getParameter(d.UNMASKED_RENDERER_WEBGL):g.getParameter(g.RENDERER),vendor:d?g.getParameter(d.UNMASKED_VENDOR_WEBGL):g.getParameter(g.VENDOR)};g.getExtension('WEBGL_lose_context')?.loseContext();return r})())");
            SmokeTest.Report("render graphics: " + graphics);
            foreach (bool useApp in new[] { true, false, false, true })
            {
                _browserControl!.Visibility = useApp ? Visibility.Visible : Visibility.Collapsed;
                plain.Visibility = useApp ? Visibility.Collapsed : Visibility.Visible;
                await Task.Delay(200);
                await MeasureRenderAsync(useApp ? app : plain.CoreWebView2, useApp ? "app" : "plain");
            }
            SmokeTest.Report("render audit complete");
        }
        finally
        {
            plain.Close(); BrowserHost.Children.Remove(plain); UpdateBrowserVisibility();
        }
    }

    private static async Task NavigateRenderFixtureAsync(CoreWebView2 core, string address)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Loaded(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => completion.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Loaded;
        try
        {
            core.Navigate(address);
            if (!await completion.Task.WaitAsync(TimeSpan.FromSeconds(15))) throw new InvalidOperationException("Render fixture failed");
        }
        finally { core.NavigationCompleted -= Loaded; }
    }

    private async Task MeasureRenderAsync(CoreWebView2 core, string label)
    {
        var delays = new List<double>();
        var clock = Stopwatch.StartNew();
        double previous = 0;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        void Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (previous > 0) delays.Add(Math.Max(0, now - previous - 16));
            previous = now;
        }
        timer.Tick += Tick;
        timer.Start();
        try
        {
            // rAF pacing while scrolling is a reproducible synthetic workload,
            // not a claim about presentation FPS or the user's Internet latency.
            const string script = """
            (async()=>{
              const times=[],start=performance.now();let previous=0;
              const maximum=Math.max(0,document.documentElement.scrollHeight-innerHeight);
              for(let frame=0;frame<180;frame++){
                const now=await new Promise(requestAnimationFrame);
                if(previous)times.push(now-previous);previous=now;
                scrollTo(0,maximum*(1-Math.cos(frame*Math.PI/90))/2);
                if(performance.now()-start>6000)break;
              }
              scrollTo(0,0);times.sort((a,b)=>a-b);
              return {visibility:document.visibilityState,dpr:devicePixelRatio,viewport:[innerWidth,innerHeight],
                images:document.images.length,loaded:[...document.images].filter(i=>i.complete&&i.naturalWidth>0).length,
                frames:times.length,median:times[Math.floor(times.length*.5)],p95:times[Math.floor(times.length*.95)],
                over33:times.filter(v=>v>33.5).length,over50:times.filter(v=>v>50).length,elapsed:performance.now()-start};
            })()
            """;
            string result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            { expression = script, awaitPromise = true, returnByValue = true })).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            using var json = JsonDocument.Parse(result);
            var metrics = json.RootElement.GetProperty("result").GetProperty("value");
            if (metrics.GetProperty("visibility").GetString() != "visible" || metrics.GetProperty("frames").GetInt32() < 30)
                throw new InvalidOperationException("Render sample was not visible/long enough");
            delays.Sort();
            SmokeTest.Report($"render sample {label}: host UI-delay p95={(delays.Count == 0 ? 0 : delays[(int)(delays.Count * .95)]):F1}ms; max={(delays.Count == 0 ? 0 : delays[^1]):F1}ms; page={metrics.GetRawText()}");
        }
        finally { timer.Stop(); timer.Tick -= Tick; }
    }
}
