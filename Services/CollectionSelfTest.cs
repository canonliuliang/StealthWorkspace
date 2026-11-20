using System.Collections.Specialized;
using System.Collections.Immutable;
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

        var first = new BookmarkItem { Title = "Project", Url = "https://example.test/A" };
        var second = new BookmarkItem { Title = "\U00010400 report", Url = "https://example.test/B" };
        var snapshot = ImmutableList.Create(first, second);
        var filter = new BookmarkFilter();
        report(ReferenceEquals(filter.Filter(snapshot, " "), snapshot),
            "Opening bookmarks uses the ordered settings snapshot directly", "");
        string[] queries = ["p", "pr", "proj", "PROJECT", "p", "example.test/B", "\uD801", "\U00010428", "missing", ""];
        bool equivalent = queries.All(query => filter.Filter(snapshot, query).SequenceEqual(snapshot.Where(item =>
            item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Url.Contains(query, StringComparison.OrdinalIgnoreCase))));
        report(equivalent, "Bookmark filtering handles append, paste, delete, case, URL and supplementary Unicode", "");
        var added = new BookmarkItem { Title = "Project two", Url = "https://example.test/C" };
        var changed = snapshot.Remove(first).Add(added);
        filter.Filter(snapshot, "proj");
        report(filter.Filter(changed, "proje").SequenceEqual([added]) && filter.Filter(changed, "").SequenceEqual([second, added]),
            "Changing the bookmark source drops removed items and searches new items in order", "");
    }
}
