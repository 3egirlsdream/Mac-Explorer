using MacExplorer.Models;

namespace MacExplorer.Controls;

internal sealed class FileNameTypeAhead
{
    private const long TimeoutMilliseconds = 1_000;
    private string _prefix = string.Empty;
    private long _lastInputMilliseconds;

    public int Find(IReadOnlyList<FileSystemEntry> rows, int current, string text, long nowMilliseconds)
    {
        var count = rows.Count;
        if (count == 0 || string.IsNullOrEmpty(text)) return -1;

        if (nowMilliseconds - _lastInputMilliseconds >= TimeoutMilliseconds || nowMilliseconds < _lastInputMilliseconds)
            Reset();
        _lastInputMilliseconds = nowMilliseconds;

        bool repeat = text.Length == 1 && _prefix.Equals(text, StringComparison.OrdinalIgnoreCase);
        bool extend = _prefix.Length != 0 && !repeat;
        var prefix = extend ? _prefix + text : text;
        var hasCurrent = current >= 0 && current < count;
        if (extend && hasCurrent && rows[current].DisplayNameSpan.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            _prefix = prefix;
            return current;
        }

        var index = hasCurrent && current + 1 < count ? current + 1 : 0;
        var fallback = -1;
        for (var visited = 0; visited < count; visited++)
        {
            var name = rows[index].DisplayNameSpan;
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _prefix = prefix;
                return index;
            }
            // Keep the first match for the new input in circular order while looking for the longer prefix.
            if (extend && fallback < 0 && name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                fallback = index;
            if (++index == count) index = 0;
        }

        _prefix = fallback >= 0 ? text : string.Empty;
        return fallback;
    }

    public void Reset() => _prefix = string.Empty;
}
