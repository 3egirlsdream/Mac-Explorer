using System.Collections.Specialized;
using MacExplorer.Collections;
using Xunit;

namespace MacExplorer.Tests;

public class FileListSnapshotCollectionTests
{
    [Fact]
    public void ReplaceAllEmitsOneResetForTenThousandItems()
    {
        var entries = new RangeObservableCollection<int>();
        var events = new List<NotifyCollectionChangedAction>();
        entries.CollectionChanged += (_, args) => events.Add(args.Action);
        entries.ReplaceAll(Enumerable.Range(0, 10_000));
        Assert.Equal(10_000, entries.Count);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
    }

    [Fact]
    public void ReplacingWithSelfIsSafeAndDoesNotNotify()
    {
        var entries = new RangeObservableCollection<int>([1, 2, 3]);
        var count = 0;
        entries.CollectionChanged += (_, _) => count++;
        entries.ReplaceAll(entries);
        Assert.Equal(new[] { 1, 2, 3 }, entries);
        Assert.Equal(0, count);
    }

    [Fact]
    public void DeferredEnumerationFailureLeavesOldItemsIntact()
    {
        var entries = new RangeObservableCollection<int>([1, 2, 3]);
        Assert.Throws<InvalidOperationException>(() => entries.ReplaceAll(FailingItems()));
        Assert.Equal(new[] { 1, 2, 3 }, entries);
    }

    private static IEnumerable<int> FailingItems()
    {
        yield return 9;
        throw new InvalidOperationException("enumeration failed");
    }

    [Fact]
    public void ReentrantMutationWithMultipleObserversIsRejected()
    {
        var entries = new RangeObservableCollection<int>();
        entries.CollectionChanged += (_, _) => Assert.Throws<InvalidOperationException>(() => entries.ReplaceAll([5]));
        entries.CollectionChanged += (_, _) => { };
        entries.ReplaceAll([1, 2]);
        Assert.Equal(new[] { 1, 2 }, entries);
    }
}
