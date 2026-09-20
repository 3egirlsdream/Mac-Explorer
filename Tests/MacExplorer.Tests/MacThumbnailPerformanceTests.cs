using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

// These tests use the real cache/gates with a deterministic generator. They do not
// require Quick Look, sips, a window server, or real image-generation timings.
public sealed class MacThumbnailPerformanceTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"thumbnail-performance-{Guid.NewGuid():N}");
    private string CacheDirectory => Path.Combine(_root, "cache");

    [Fact]
    public async Task SameKey_ConcurrentRequestsGenerateOnlyOnce()
    {
        using var timeout = TestCancellation();
        var service = CreateService();
        var calls = 0;
        var requests = Enumerable.Range(0, 16).Select(_ => service.GetOrCreateThumbnailAsync(
            "same-key", async (path, token) =>
            {
                Interlocked.Increment(ref calls);
                return await WritePngAsync(path, token);
            }, true, timeout.Token)).ToArray();

        var results = await Task.WhenAll(requests);

        Assert.Equal(1, calls);
        Assert.All(results, result =>
        {
            Assert.NotNull(result);
            Assert.Equal(results[0]!.CachePath, result.CachePath);
            Assert.Equal(Png, result.Bytes);
            Assert.True(File.Exists(result.CachePath));
        });
        Assert.Empty(Directory.EnumerateFiles(CacheDirectory, ".tmp-*"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task DifferentKeys_UseAvailableSlotsWithoutExceedingTheLimit(int limit)
    {
        using var timeout = TestCancellation();
        var service = CreateService(limit);
        var saturated = Signal();
        var release = Signal();
        var active = 0;
        var peak = 0;
        var sync = new object();
        // Avoid intentional stripe collisions when measuring available slots.
        var requests = DistinctStripeKeys(8).Select(key => service.GetOrCreateThumbnailAsync(
            key, async (path, token) =>
            {
                var current = Interlocked.Increment(ref active);
                lock (sync) peak = Math.Max(peak, current);
                if (current == limit) saturated.TrySetResult(true);
                try
                {
                    await release.Task.WaitAsync(token);
                    return await WritePngAsync(path, token);
                }
                finally { Interlocked.Decrement(ref active); }
            }, true, timeout.Token)).ToArray();

        try
        {
            await saturated.Task.WaitAsync(timeout.Token);
            Assert.Equal(limit, Volatile.Read(ref active));
        }
        finally
        {
            release.TrySetResult(true);
            await Task.WhenAll(requests);
        }

        Assert.Equal(limit, peak);
        Assert.Equal(0, active);
        Assert.All(await Task.WhenAll(requests), result => Assert.NotNull(result));
    }

    [Fact]
    public async Task DiskHitAndMemoryRepair_DoNotWaitForAnOccupiedGenerationSlot()
    {
        using var timeout = TestCancellation();
        var token = timeout.Token;
        var service = CreateService(1);
        var keys = DistinctStripeKeys(2);
        var first = await service.GetOrCreateThumbnailAsync(keys[0], WritePngAsync, true, token);
        Assert.NotNull(first);
        service.ClearCache();
        var entered = Signal();
        var release = Signal();
        var blocker = service.GetOrCreateThumbnailAsync(keys[1], async (path, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return await WritePngAsync(path, ct);
        }, false, token);

        try
        {
            await entered.Task.WaitAsync(token);
            var diskHit = await service.GetOrCreateThumbnailAsync(keys[0], UnexpectedGenerator, true, token)
                .WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.NotNull(diskHit);
            Assert.Equal(first.CachePath, diskHit.CachePath);
            File.Delete(first.CachePath);
            var repaired = await service.GetOrCreateThumbnailAsync(keys[0], UnexpectedGenerator, true, token)
                .WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.NotNull(repaired);
            Assert.Equal(first.CachePath, repaired.CachePath);
            Assert.Equal(Png, repaired.Bytes);
            Assert.True(File.Exists(repaired.CachePath));
        }
        finally
        {
            release.TrySetResult(true);
            await blocker;
        }
    }

    [Fact]
    public async Task CancellingGeneration_ReleasesItsSlotAndDoesNotCacheFailure()
    {
        using var timeout = TestCancellation();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var service = CreateService(1);
        var entered = Signal();
        var first = service.GetOrCreateThumbnailAsync("cancel-retry", async (path, token) =>
        {
            await File.WriteAllBytesAsync(path, Png, token);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Png;
        }, true, cancelled.Token);

        await entered.Task.WaitAsync(timeout.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await first; });
        Assert.Empty(Directory.EnumerateFiles(CacheDirectory, ".tmp-*"));

        var retry = await service.GetOrCreateThumbnailAsync("cancel-retry", WritePngAsync, true, timeout.Token);
        Assert.NotNull(retry);
        Assert.True(File.Exists(retry.CachePath));
    }

    [Fact]
    public async Task CancellingSameKeyWaiter_DoesNotCancelTheOwner()
    {
        using var timeout = TestCancellation();
        using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var service = CreateService();
        var entered = Signal();
        var release = Signal();
        var owner = service.GetOrCreateThumbnailAsync("shared", async (path, token) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return await WritePngAsync(path, token);
        }, true, timeout.Token);

        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var waiter = service.GetOrCreateThumbnailAsync("shared", UnexpectedGenerator, true, waiterCancellation.Token);
            waiterCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await waiter; });
            Assert.False(owner.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
            await owner;
        }
        Assert.NotNull(await owner);
    }

    [Fact]
    public async Task NormalFailure_IsCachedButFaceStyleRequestsRemainRetryable()
    {
        using var timeout = TestCancellation();
        var service = CreateService();
        var calls = 0;
        Task<byte[]?> Fail(string _, CancellationToken __)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<byte[]?>(null);
        }

        Assert.Null(await service.GetOrCreateThumbnailAsync("normal", Fail, true, timeout.Token));
        Assert.Null(await service.GetOrCreateThumbnailAsync("normal", UnexpectedGenerator, true, timeout.Token));
        Assert.Equal(1, calls);
        Assert.Null(await service.GetOrCreateThumbnailAsync("face", Fail, false, timeout.Token));
        Assert.Null(await service.GetOrCreateThumbnailAsync("face", Fail, false, timeout.Token));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task LocalTimeout_ReleasesSlotWithoutCancellingTheCaller()
    {
        using var timeout = TestCancellation();
        var service = CreateService(1);
        var result = await service.GetOrCreateThumbnailAsync("stalled", async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Png;
        }, true, timeout.Token, generationTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Null(result);
        Assert.False(timeout.IsCancellationRequested);
        Assert.NotNull(await service.GetOrCreateThumbnailAsync("next", WritePngAsync, true, timeout.Token));
    }

    [Fact]
    public async Task Trim_PreservesPrivateInFlightFilesAndEnforcesEveryWriteBudget()
    {
        using var timeout = TestCancellation();
        var token = timeout.Token;
        const long maxBytes = 1024;
        const long targetBytes = (long)(maxBytes * 0.8);
        var service = CreateService(2, maxBytes);
        var keys = DistinctStripeKeys(3);
        await SeedOldFilesAsync("first", token);
        var ignored = Path.Combine(CacheDirectory, ".tmp-abandoned");
        await File.WriteAllBytesAsync(ignored, new byte[2048], token);
        var entered = Signal();
        var release = Signal();
        string? inFlightPath = null;
        var inFlight = service.GetOrCreateThumbnailAsync(keys[0], async (path, ct) =>
        {
            await WritePngAsync(path, ct);
            inFlightPath = path;
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return Png;
        }, true, token);

        try
        {
            await entered.Task.WaitAsync(token);
            var second = await service.GetOrCreateThumbnailAsync(keys[1], WritePngAsync, true, token);
            Assert.NotNull(second);
            Assert.True(File.Exists(second.CachePath));
            Assert.True(File.Exists(inFlightPath));
            Assert.True(FinalCacheBytes() <= targetBytes);
            Assert.True(File.Exists(ignored));
        }
        finally
        {
            release.TrySetResult(true);
            await inFlight;
        }

        var first = await inFlight;
        Assert.NotNull(first);
        Assert.True(File.Exists(first.CachePath));
        // External cache growth must still be noticed on the very next write.
        await SeedOldFilesAsync("second", token);
        var third = await service.GetOrCreateThumbnailAsync(keys[2], WritePngAsync, true, token);
        Assert.NotNull(third);
        Assert.True(File.Exists(third.CachePath));
        Assert.True(FinalCacheBytes() <= targetBytes);
        Assert.True(File.Exists(ignored));
    }

    [Fact]
    public async Task NativeImageSuccess_DoesNotRunFallback()
    {
        using var timeout = TestCancellation();
        var result = await MacThumbnailService.GenerateImageWithFallbackAsync(
            _ => Task.FromResult<byte[]?>(Png),
            _ => throw new InvalidOperationException("Fallback must not run after success."),
            TimeSpan.FromSeconds(2), timeout.Token);
        Assert.Equal(Png, result);
    }

    [Fact]
    public async Task MissingNativeImage_UsesExistingFallback()
    {
        using var timeout = TestCancellation();
        var result = await MacThumbnailService.GenerateImageWithFallbackAsync(
            _ => Task.FromResult<byte[]?>(null), _ => Task.FromResult<byte[]?>(Png),
            TimeSpan.FromSeconds(2), timeout.Token);
        Assert.Equal(Png, result);
    }

    [Fact]
    public async Task NativeImageTimeout_UsesFallbackWithAnUncancelledToken()
    {
        using var timeout = TestCancellation();
        var result = await MacThumbnailService.GenerateImageWithFallbackAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }, token =>
        {
            Assert.False(token.IsCancellationRequested);
            Assert.Equal(timeout.Token, token);
            return Task.FromResult<byte[]?>(Png);
        }, TimeSpan.FromMilliseconds(50), timeout.Token);
        Assert.Equal(Png, result);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotStartImageFallback()
    {
        using var timeout = TestCancellation();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var entered = Signal();
        var fallbackCalled = false;
        var request = MacThumbnailService.GenerateImageWithFallbackAsync(async token =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }, _ =>
        {
            fallbackCalled = true;
            return Task.FromResult<byte[]?>(Png);
        }, TimeSpan.FromSeconds(5), cancelled.Token);
        await entered.Task.WaitAsync(timeout.Token);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await request; });
        Assert.False(fallbackCalled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Concurrency_IsExplicitlyBounded(int limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(limit));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheHit_DoesNotWaitForUnrelatedGenerationOnTheSameStripe(bool keepMemory)
    {
        using var timeout = TestCancellation();
        var token = timeout.Token;
        var service = CreateService(1);
        var keys = CollidingStripeKeys();
        var seeded = await service.GetOrCreateThumbnailAsync(keys[0], WritePngAsync, true, token);
        Assert.NotNull(seeded);
        if (!keepMemory) service.ClearCache();
        var entered = Signal();
        var release = Signal();
        // No generation timeout: this request owns the shared stripe until finally
        // releases it, not until a machine-dependent sleep happens to expire.
        var blocker = service.GetOrCreateThumbnailAsync(keys[1], async (path, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return await WritePngAsync(path, ct);
        }, false, token);
        try
        {
            await entered.Task.WaitAsync(token);
            var hit = await service.GetOrCreateThumbnailAsync(keys[0], UnexpectedGenerator, true, token)
                .WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.NotNull(hit);
            Assert.Equal(seeded.CachePath, hit.CachePath);
            Assert.Equal(Png, hit.Bytes);
            Assert.False(blocker.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
            await blocker;
        }
    }

    [Fact]
    public async Task CancelledCacheHit_DoesNotDeleteAReusableDiskEntry()
    {
        using var timeout = TestCancellation();
        var service = CreateService();
        var seeded = await service.GetOrCreateThumbnailAsync("cancelled-disk", WritePngAsync, true, timeout.Token);
        Assert.NotNull(seeded);
        service.ClearCache();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.GetOrCreateThumbnailAsync("cancelled-disk", UnexpectedGenerator, true, cancelled.Token);
        });
        Assert.True(File.Exists(seeded.CachePath));
        Assert.NotNull(await service.GetOrCreateThumbnailAsync("cancelled-disk", UnexpectedGenerator, true, timeout.Token));
    }

    private static string[] CollidingStripeKeys()
    {
        var firstByStripe = new Dictionary<int, string>();
        for (var i = 0; i <= 64; i++)
        {
            var key = $"collision-{i}";
            var stripe = (StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % 64;
            if (firstByStripe.TryGetValue(stripe, out var first)) return [first, key];
            firstByStripe.Add(stripe, key);
        }
        throw new InvalidOperationException("65 distinct keys must collide across 64 stripes.");
    }

    private MacThumbnailService CreateService(int concurrency = 4, long maxDiskBytes = 1024 * 1024)
        => new(CacheDirectory, maxDiskBytes, 0.8, concurrency);

    private static async Task<byte[]?> WritePngAsync(string path, CancellationToken token)
    {
        await File.WriteAllBytesAsync(path, Png, token);
        return Png;
    }

    private static Task<byte[]?> UnexpectedGenerator(string _, CancellationToken __)
        => throw new InvalidOperationException("A cache hit must not invoke the generator.");

    private async Task SeedOldFilesAsync(string prefix, CancellationToken token)
    {
        for (var i = 0; i < 4; i++)
        {
            var path = Path.Combine(CacheDirectory, $"{prefix}-{i}.png");
            await File.WriteAllBytesAsync(path, new byte[512], token);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddHours(-2 - i));
        }
    }

    private long FinalCacheBytes() => new DirectoryInfo(CacheDirectory)
        .EnumerateFiles("*.png").Sum(info => info.Length);

    private static string[] DistinctStripeKeys(int count)
    {
        var stripes = new HashSet<int>();
        var keys = new List<string>();
        for (var i = 0; keys.Count < count; i++)
        {
            var key = $"key-{i}";
            if (stripes.Add((StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % 64)) keys.Add(key);
        }
        return keys.ToArray();
    }

    private static TaskCompletionSource<bool> Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CancellationTokenSource TestCancellation()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(15));
        return source;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
