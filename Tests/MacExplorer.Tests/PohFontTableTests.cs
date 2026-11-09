using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using HarfBuzzSharp;
using MacExplorer.TextShaping;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Typography")]
public sealed class PohFontTableTests
{
    [Theory]
    [InlineData(16384)]
    [InlineData(1048576)]
    public void BlobOwnsPohCopyUntilNativeRelease(int length)
    {
        Collect();
        var baseline = PohSize();
        var (blob, source) = Create(length);
        try
        {
            Collect();
            Assert.False(source.IsAlive);
            Assert.True(PohSize() >= baseline + length);
            AssertBytes(blob, length);
        }
        finally { blob.Dispose(); }
        Collect();
        Assert.True(PohSize() < baseline + length);
        GC.KeepAlive(blob);
    }

    [Fact]
    public void EmptyTableCanBeDisposedRepeatedly()
    {
        var blob = PohFontTable.Create([]);
        Assert.Equal(0, blob.Length);
        blob.Dispose();
        blob.Dispose();
    }

    [AvaloniaFact]
    public void InstallationActivatesMatchingBackendAndIsIdempotent()
    {
        Assert.False(PohTextShaping.IsActive);
        Assert.True(PohTextShaping.TryInstall());
        Assert.True(PohTextShaping.IsActive);
        Assert.True(PohTextShaping.TryInstall());
        Assert.True(PohTextShaping.IsActive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertBytes(Blob blob, int length)
    {
        // Do not leave the interior Span on the collecting test's stack.
        Assert.Equal(length, blob.Length);
        var bytes = blob.AsSpan();
        for (var index = 0; index < length; index += 257)
            Assert.Equal(Pattern(index), bytes[index]);
        Assert.Equal(Pattern(length - 1), bytes[^1]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Blob Blob, WeakReference Source) Create(int length)
    {
        var source = new byte[length + 512];
        for (var index = 0; index < length; index++) source[index + 257] = Pattern(index);
        return (PohFontTable.Create(source.AsSpan(257, length)), new WeakReference(source));
    }

    private static byte Pattern(int index) => unchecked((byte)(index * 31 + 7));
    private static long PohSize()
    {
        var info = GC.GetGCMemoryInfo().GenerationInfo[4];
        return info.SizeAfterBytes - info.FragmentationAfterBytes;
    }
    private static void Collect()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }
}
