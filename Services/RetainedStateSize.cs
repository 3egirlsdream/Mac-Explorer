using System.Collections;
using System.Runtime.CompilerServices;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;

namespace MacExplorer.Services;

internal interface IRetainedState
{
    void MeasureFields(RetainedStateSize size, int depth);
}

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

    // Count primitive fields at their original per-field cost without boxing them.
    internal void AddScalars(int count, int depth)
    {
        if (_bytes > limit) return;
        if (depth > 12 || (_remaining -= count) < 0) _bytes = limit + 1;
        else _bytes += 32L * count;
    }

    internal void Visit(object? value, int depth)
    {
        if (value == null || _bytes > limit) return;
        if (depth > 12 || --_remaining < 0) { _bytes = limit + 1; return; }
        if (value is not ValueType && !_seen.Add(value)) return;

        var childDepth = depth + 1;
        switch (value)
        {
            case string text:
                _bytes += 24 + text.Length * 2L;
                break;
            case bool or char or byte or sbyte or short or ushort or int or uint or long or ulong
                or nint or nuint or float or double or Enum or DateTime or DateTimeOffset or TimeSpan or Guid or decimal:
                _bytes += 32;
                break;
            case Array array when TryAddPrimitiveArray(array):
                break;
            case IDictionary dictionary:
                _bytes += 64L + dictionary.Count * 48L;
                foreach (DictionaryEntry entry in dictionary)
                {
                    Visit(entry.Key, childDepth);
                    Visit(entry.Value, childDepth);
                    if (_bytes > limit) break;
                }
                break;
            case IList list:
                _bytes += 32L + list.Count * 8L;
                foreach (var item in list)
                {
                    Visit(item, childDepth);
                    if (_bytes > limit) break;
                }
                break;
            case ITuple tuple:
                _bytes += 32;
                for (var index = 0; index < tuple.Length; index++) Visit(tuple[index], childDepth);
                break;
            case CompositeFileService.InventoryItem node:
                // Count child references here. Sources indexes every node and measures it once.
                _bytes += 128 + node.Children.Capacity * 8L;
                Visit(node.Entry, childDepth);
                Visit(node.CopyMetadata, childDepth);
                Visit(node.Identity, childDepth);
                break;
            case FileOperationDisplay:
                _bytes += 160;
                break;
            default:
                _bytes += 64;
                VisitRecord(value, childDepth);
                break;
        }
    }

    private bool TryAddPrimitiveArray(Array array)
    {
        if (array is object[]) return false;
        try
        {
            _bytes += 32L + Buffer.ByteLength(array);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // Arrays of records or structs are measured as lists.
        }
    }

    private void VisitRecord(object value, int depth)
    {
        switch (value)
        {
            case IRetainedState state:
                state.MeasureFields(this, depth);
                break;
            case FileTaskRequest request:
                AddScalars(3, depth); // Kind, AllowRetry, RequiredFeatures
                Visit(request.Label, depth);
                Visit(request.Sources, depth);
                Visit(request.Destination, depth);
                Visit(request.DisplayName, depth);
                Visit(request.Operation, depth);
                break;
            case FileTaskItemResult result:
                AddScalars(1, depth); // State
                Visit(result.Source, depth);
                Visit(result.Destination, depth);
                Visit(result.Error, depth);
                break;
            case FileSystemEntry entry when entry.GetType() == typeof(FileSystemEntry):
                entry.MeasureRetainedFields(this, depth);
                break;
            case DirectoryPackage package:
                AddScalars(2, depth); // IsPackage, ApplicationSize
                Visit(package.IconKey, depth);
                Visit(package.ApplicationPath, depth);
                break;
            case DocumentMatch match:
                Visit(match.Snippet, depth);
                Visit(match.Location, depth);
                break;
            case DocumentLocation location:
                AddScalars(1, depth); // Number
                Visit(location.Kind, depth);
                Visit(location.Sheet, depth);
                Visit(location.Cell, depth);
                break;
            case FileIdentitySnapshot snapshot:
                Visit(snapshot.Identity, depth);
                Visit(snapshot.Signature, depth);
                break;
            case LocalFileCopy.CopyEntry copy:
                AddScalars(4, depth); // IsDirectory, Length, Modified, IsRuntimeFile
                Visit(copy.Path, depth);
                Visit(copy.LinkTarget, depth);
                Visit(copy.Identity, depth);
                break;
            case CompositeFileService.TargetSnapshot target:
                Visit(target.Entry, depth);
                Visit(target.Identity, depth);
                break;
            case CompositeFileService.TransferChoice choice:
                AddScalars(1, depth); // FromBatch
                Visit(choice.Action, depth);
                Visit(choice.Target, depth);
                Visit(choice.Expected, depth);
                break;
            default:
                _bytes = limit + 1; // An unmeasured graph is ineligible for retry.
                break;
        }
    }
}
