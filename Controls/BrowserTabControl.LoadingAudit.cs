using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CompareLoadingWithPlainWebViewAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        await InitializeAsync(null);
        var appCore = _hookedCore ?? throw new InvalidOperationException("App core missing");
        var plain = new WebView2();
        BrowserHost.Children.Add(plain);
        try
        {
            await plain.EnsureCoreWebView2Async(appCore.Environment);
            foreach (string mode in new[] { "app", "plain", "plain", "app", "app", "plain" })
            {
                bool app = mode == "app";
                _browserControl!.Visibility = app ? Visibility.Visible : Visibility.Collapsed;
                plain.Visibility = app ? Visibility.Collapsed : Visibility.Visible;
                var core = app ? appCore : plain.CoreWebView2;
                // Both engines share this isolated test profile. Clear only its
                // HTTP cache to give each image-heavy sample equal cold resources.
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
                await MeasureLoadingAsync(core, fixture.Address + "/reading", mode);
            }
        }
        finally
        {
            plain.Close();
            BrowserHost.Children.Remove(plain);
            UpdateBrowserVisibility();
        }
    }

    private async Task MeasureLoadingAsync(CoreWebView2 core, string address, string label)
    {
        var watch = new Stopwatch();
        var ready = new TaskCompletionSource<bool>();
        double domMs = 0;
        var stalls = new List<double>();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        double lastTick = 0;
        void Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            double now = watch.Elapsed.TotalMilliseconds;
            stalls.Add(Math.Max(0, now - lastTick - 16));
            lastTick = now;
        }
        void Dom(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => domMs = watch.Elapsed.TotalMilliseconds;
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => ready.TrySetResult(args.IsSuccess);
        timer.Tick += Tick;
        core.DOMContentLoaded += Dom;
        core.NavigationCompleted += Completed;
        try
        {
            watch.Start();
            timer.Start();
            core.Navigate(address);
            bool ok = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            double loadMs = watch.Elapsed.TotalMilliseconds;
            await Task.Delay(80);
            timer.Stop();
            string metrics = await core.ExecuteScriptAsync("JSON.stringify({paint:performance.getEntriesByName('first-contentful-paint')[0]?.startTime,images:document.images.length,loaded:Array.from(document.images).filter(i=>i.complete&&i.naturalWidth>0).length})");
            stalls.Sort();
            double p95 = stalls.Count == 0 ? 0 : stalls[Math.Min(stalls.Count - 1, (int)(stalls.Count * .95))];
            SmokeTest.Report($"loading audit: {label}; success={ok}; DOM={domMs:F1}ms; load={loadMs:F1}ms; UI-delay p95={p95:F1}ms; {metrics}");
            if (!ok) throw new InvalidOperationException("Loading fixture failed");
        }
        finally
        {
            timer.Stop();
            timer.Tick -= Tick;
            core.DOMContentLoaded -= Dom;
            core.NavigationCompleted -= Completed;
        }
    }
}
