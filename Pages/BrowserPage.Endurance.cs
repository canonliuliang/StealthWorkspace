using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class BrowserPage
{
    internal async Task RunEnduranceAsync(TimeSpan duration)
    {
        // Runs only with --ui-smoke and an explicitly isolated data root. Exercise
        // real WebViews, timers, icons and page state without private browsing data.
        var watch = Stopwatch.StartNew();
        var retained = new List<TabViewItem>();
        var retired = new List<WeakReference<BrowserTabControl>>();
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) CreateNewTab();
            var item = (TabViewItem)BrowserTabView.SelectedItem;
            if (item.Content is not BrowserTabControl tab || !await tab.PrepareEndurancePageAsync()) throw new InvalidOperationException("Soak setup failed");
            retained.Add(item);
        }
        BrowserTabView.SelectedItem = retained[0];
        using var process = Process.GetCurrentProcess();
        long initial = process.PrivateMemorySize64;
        int cycles = 0;
        long maximum = initial;
        SmokeTest.Report($"soak started: minutes={duration.TotalMinutes:0}; retained tabs=3; private MiB={initial / 1048576.0:F1}");
        while (watch.Elapsed < duration)
        {
            // Change the foreground retained page only every 2 minutes; the others
            // must reach the real 60-second sleep threshold and keep unsaved input.
            var active = retained[(cycles / 8) % retained.Count];
            BrowserTabView.SelectedItem = active;
            if (active.Content is not BrowserTabControl activeTab || !await activeTab.VerifySmokeResumeAsync())
                throw new InvalidOperationException("Soak lost retained page state");
            CreateNewTab();
            var transient = (TabViewItem)BrowserTabView.SelectedItem;
            var transientTab = (BrowserTabControl)transient.Content;
            if (!await transientTab.PrepareEndurancePageAsync()) throw new InvalidOperationException("Soak new tab failed");
            if (cycles % 8 == 0 && (!await transientTab.CheckSmokeChromeAsync() || !await transientTab.CheckSmokeFindAsync()))
                throw new InvalidOperationException("Soak icon/find regression");
            await CloseTabAsync(transient);
            retired.Add(new WeakReference<BrowserTabControl>(transientTab));
            transientTab = null!;
            transient = null!;
            BrowserTabView.SelectedItem = active;
            if (BrowserTabControl.LiveTabCount != 3) throw new InvalidOperationException($"Soak unexpected tab count: expected=3, live={BrowserTabControl.LiveTabCount}, items={BrowserTabView.TabItems.Count}; inspect tab-create origins");
            cycles++;
            process.Refresh();
            long current = process.PrivateMemorySize64;
            maximum = Math.Max(maximum, current);
            int asleep = retained.Count(item => item.Content is BrowserTabControl { IsSmokeSuspended: true });
            var renderer = activeTab.GetEnduranceRendererMetrics();
            SmokeTest.Report($"soak sample: elapsed={watch.Elapsed.TotalMinutes:F2}min; cycles={cycles}; live=3; suspended={asleep}; app private MiB={current / 1048576.0:F1}; managed MiB={GC.GetTotalMemory(false) / 1048576.0:F1}; closed controls awaiting GC={retired.Count(reference => reference.TryGetTarget(out _))}; renderer private MiB={renderer.Memory / 1048576.0:F1}; renderer CPU ms={renderer.Cpu.TotalMilliseconds:F0}; threads={process.Threads.Count}; handles={process.HandleCount}; gen2={GC.CollectionCount(2)}");
            TimeSpan remaining = duration - watch.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining < TimeSpan.FromSeconds(15) ? remaining : TimeSpan.FromSeconds(15));
        }
        foreach (var item in retained)
        {
            BrowserTabView.SelectedItem = item;
            if (!await ((BrowserTabControl)item.Content).VerifySmokeResumeAsync()) throw new InvalidOperationException("Soak final resume failed");
        }
        if (!await BurnAllTabsAsync() || BrowserTabControl.LiveTabCount != 1) throw new InvalidOperationException("Soak final session cleanup failed");
        SmokeTest.Report($"soak complete: elapsed={watch.Elapsed.TotalMinutes:F2}min; cycles={cycles}; peak app private MiB={maximum / 1048576.0:F1}; retained DOM verified; session cleaned");
    }
}
