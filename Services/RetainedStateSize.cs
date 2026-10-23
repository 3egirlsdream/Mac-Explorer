using System.Collections;
using System.Reflection;

namespace MacExplorer.Services;

/// <summary>Conservative estimate for checkpoint data, not a managed-heap profiler.</summary>
internal sealed class RetainedStateSize(long limit)
{
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    private int _remaining = 50_000;
    private long _bytes;

    internal long Measure(object? value)
    {
        Visit(value, 0);
        return _bytes;
    }

    private void Visit(object? value, int depth)
    {
        if (value == null || _bytes > limit) return;
        if (depth > 12 || --_remaining < 0) { _bytes = limit + 1; return; }
        var type = value.GetType();
        if (!type.IsValueType && !_seen.Add(value)) return;
        if (value is string text) { _bytes += 24 + text.Length * 2L; return; }
        if (type.IsPrimitive || type.IsEnum || value is DateTime or DateTimeOffset or TimeSpan or Guid or decimal) { _bytes += 32; return; }
        if (value is Array array && array.GetType().GetElementType()!.IsPrimitive)
        { _bytes += 32L + Buffer.ByteLength(array); return; }
        if (value is IDictionary dictionary)
        {
            _bytes += 64L + dictionary.Count * 48L;
            foreach (DictionaryEntry entry in dictionary)
            {
                Visit(entry.Key, depth + 1); Visit(entry.Value, depth + 1);
                if (_bytes > limit) break;
            }
        }
        else if (value is IList list)
        {
            _bytes += 32L + list.Count * 8L;
            foreach (var item in list) { Visit(item, depth + 1); if (_bytes > limit) break; }
        }
        else if (value is System.Runtime.CompilerServices.ITuple tuple)
        {
            _bytes += 32;
            for (var index = 0; index < tuple.Length; index++) Visit(tuple[index], depth + 1);
        }
        else if (type.Assembly == typeof(FileTaskContext).Assembly && value is not Delegate)
        {
            // Our checkpoint records contain paths, manifests and partial-transfer state.
            // Read fields only; invoking properties could perform I/O or allocate more data.
            _bytes += 64;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Visit(field.GetValue(value), depth + 1);
        }
        else _bytes = limit + 1; // Unknown graphs are not safe to keep for an indefinite retry.
    }
}
