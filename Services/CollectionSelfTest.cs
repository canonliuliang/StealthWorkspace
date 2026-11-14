using System.Collections.Specialized;
using System.Diagnostics;

namespace StealthWorkspace.Services;

internal static class CollectionSelfTest
{
    internal static void Run(Action<bool, string, string> report)
    {
        var collection = new ObservableRangeCollection<int>();
        int events = 0, resets = 0;
        collection.CollectionChanged += (_, e) => { events++; if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        int[] all = Enumerable.Range(0, 10000).ToArray();
        collection.Reconcile(all);
        report(events == 1 && resets == 1 && collection.SequenceEqual(all), "Large library loads with one collection notification", $"{events} notification for 10,000 items");
        events = resets = 0;
        report(!collection.Reconcile(all) && events == 0, "Unchanged library preserves containers without notifications", "");
        int[] filtered = all.Where(i => i % 5 == 0).ToArray();
        var watch = Stopwatch.StartNew();
        collection.Reconcile(filtered);
        watch.Stop();
        report(events == 1 && resets == 1 && collection.SequenceEqual(filtered), "Large search change batches 8,000 removals into one reset", $"{watch.Elapsed.TotalMilliseconds:F2} ms; notifications={events}");
        collection.Reconcile(new[] { 1, 2, 3 });
        events = resets = 0;
        collection.Reconcile(new[] { 1, 2, 3, 4 });
        report(events == 1 && resets == 0 && collection.SequenceEqual(new[] { 1, 2, 3, 4 }), "Single import uses incremental notification", "");
        collection.Reconcile(new[] { 4, 2, 1, 3 });
        report(collection.SequenceEqual(new[] { 4, 2, 1, 3 }), "Reordered results never duplicate retained items", "");
        collection.Reconcile(Array.Empty<int>());
        report(collection.Count == 0, "Empty result clears the library", "");
    }
}
