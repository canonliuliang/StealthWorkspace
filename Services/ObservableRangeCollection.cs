using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace StealthWorkspace.Services;

/// <summary>Preserves containers for small edits and bounds notifications for large result changes.</summary>
internal sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    internal bool Reconcile(IReadOnlyList<T> next)
    {
        var comparer = EqualityComparer<T>.Default;
        int prefix = 0;
        while (prefix < Count && prefix < next.Count && comparer.Equals(this[prefix], next[prefix])) prefix++;
        if (prefix == Count && prefix == next.Count) return false;
        int suffix = 0;
        while (suffix < Count - prefix && suffix < next.Count - prefix &&
               comparer.Equals(this[Count - suffix - 1], next[next.Count - suffix - 1])) suffix++;

        int removed = Count - prefix - suffix, added = next.Count - prefix - suffix;
        if (removed + added <= 64)
        {
            for (int i = 0; i < removed; i++) RemoveAt(prefix);
            for (int i = 0; i < added; i++) Insert(prefix + i, next[prefix + i]);
        }
        else
        {
            CheckReentrancy();
            Items.Clear();
            foreach (T item in next) Items.Add(item);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        return true;
    }
}
