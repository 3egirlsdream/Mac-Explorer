using System.Runtime.InteropServices;
using HarfBuzzSharp;

namespace MacExplorer.TextShaping;

internal static class PohFontTable
{
    internal static Blob Create(ReadOnlySpan<byte> table)
    {
        // Font tables live as long as the cached HarfBuzz face. Pinning an ordinary
        // SOH array here prevents compacting the temporary allocations around it.
        var copy = GC.AllocateUninitializedArray<byte>(table.Length, pinned: true);
        table.CopyTo(copy);
        var handle = GCHandle.Alloc(copy, GCHandleType.Pinned);
        void Release()
        {
            if (handle.IsAllocated) handle.Free();
        }
        try
        {
            return new Blob(handle.AddrOfPinnedObject(), copy.Length, MemoryMode.ReadOnly, Release);
        }
        catch
        {
            Release();
            throw;
        }
    }
}
