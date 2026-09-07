using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MacExplorer.Collections;

/// <summary>A UI-thread collection with one Reset per snapshot, not one Add per file.</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public RangeObservableCollection() { }

    public RangeObservableCollection(IEnumerable<T> items) : base(items) { }

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        // Materialize before mutating: handles ReplaceAll(this), deferred queries,
        // and enumerators which fail, without leaving a half-cleared collection.
        var replacement = items.ToArray();
        if (Items.Count == replacement.Length && Items.SequenceEqual(replacement))
            return;

        Items.Clear();
        foreach (var item in replacement)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
