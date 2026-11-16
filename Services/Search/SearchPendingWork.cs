namespace MacExplorer.Services.Search;

internal enum SearchWorkKind { Entry, Directory, Recursive }
internal enum SearchWorkReason { Change, Initial, Coverage, WatcherRecovery, AccessRecovery, JournalRecovery, Refresh, OverflowFallback }

internal sealed record SearchWork(string Path, SearchWorkKind Kind, SearchWorkReason Reason = SearchWorkReason.Change)
{
    internal bool Urgent => Reason is SearchWorkReason.Coverage or SearchWorkReason.WatcherRecovery
        or SearchWorkReason.AccessRecovery or SearchWorkReason.JournalRecovery or SearchWorkReason.Refresh;
    internal long Bytes => 96L + Path.Length * 2L;
    internal bool Covers(SearchWork other) => Path == other.Path && Kind >= other.Kind
        || Kind == SearchWorkKind.Recursive && SearchPath.IsWithin(other.Path, Path)
        || Kind == SearchWorkKind.Directory && other.Kind == SearchWorkKind.Entry && System.IO.Path.GetDirectoryName(other.Path) == Path;
}

/// <summary>Bounded pending work; pressure widens the smallest shared subtree first.</summary>
internal sealed class SearchPendingWork(string root, int limit, long byteLimit)
{
    private readonly Dictionary<string, SearchWork> _items = new(StringComparer.Ordinal);
    internal int Count => _items.Count;
    internal long Bytes { get; private set; }
    internal bool HasUrgent => _items.Values.Any(item => item.Urgent);

    internal void Add(SearchWork work)
    {
        if (!SearchPath.IsWithin(work.Path, root)) return;
        foreach (var covering in _items.Values)
            if (covering.Covers(work))
            {
                if (work.Urgent && !covering.Urgent) _items[covering.Path] = covering with { Reason = work.Reason };
                return;
            }
        foreach (var covered in _items.Values.Where(work.Covers).ToArray()) Remove(covered.Path);
        if (_items.TryGetValue(work.Path, out var previous))
        {
            work = work with { Kind = (SearchWorkKind)Math.Max((int)previous.Kind, (int)work.Kind),
                Reason = previous.Urgent ? previous.Reason : work.Reason };
            Remove(work.Path);
        }
        _items.Add(work.Path, work); Bytes += work.Bytes;
        while (_items.Count > Math.Max(1, limit) || Bytes > byteLimit && _items.Count > 1)
        {
            var ancestors = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in _items.Values)
                for (var parent = System.IO.Path.GetDirectoryName(item.Path); parent != null && SearchPath.IsWithin(parent, root);
                     parent = parent == root ? null : System.IO.Path.GetDirectoryName(parent))
                    ancestors[parent] = ancestors.GetValueOrDefault(parent) + 1;
            var target = ancestors.Where(item => item.Value > 1)
                .OrderByDescending(item => item.Key.Count(ch => ch == '/')).ThenByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key).FirstOrDefault() ?? root;
            var affected = _items.Values.Where(item => SearchPath.IsWithin(item.Path, target)).ToArray();
            var urgent = affected.FirstOrDefault(item => item.Urgent);
            foreach (var item in affected) Remove(item.Path);
            var replacement = new SearchWork(target, SearchWorkKind.Recursive,
                urgent?.Reason ?? (target == root ? SearchWorkReason.OverflowFallback : SearchWorkReason.Change));
            _items[target] = replacement; Bytes += replacement.Bytes;
        }
    }

    internal SearchWork? RemoveOverflow()
    {
        if (_items.TryGetValue(root, out var work) && work.Reason == SearchWorkReason.OverflowFallback)
        { Remove(root); return work; }
        return null;
    }

    private void Remove(string path)
    { if (_items.Remove(path, out var item)) Bytes -= item.Bytes; }

    internal SearchWork[] Take()
    {
        var result = _items.Values.OrderByDescending(item => item.Urgent).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
        Clear(); return result;
    }

    internal void Clear() { _items.Clear(); Bytes = 0; }
}
