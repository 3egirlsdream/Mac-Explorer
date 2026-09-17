using System.Security.Cryptography;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewHashBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(131072)]
    [InlineData(131073)]
    public async Task ExactBudgetStillReturnsTheCorrectHash(int length)
    {
        var bytes = new byte[length];
        using var stream = new MemoryStream(bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            await FileHashCalculator.ComputeStreamAsync(stream, length));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(131072L)]
    [InlineData(131073L)]
    public async Task GrowingNonSeekableStreamStopsAtBudgetPlusOneLookAheadByte(long limit)
    {
        using var stream = new EndlessStream();
        Assert.Null(await FileHashCalculator.ComputeStreamAsync(stream, limit));
        Assert.Equal(limit + 1, stream.BytesRead);
    }

    [Fact]
    public async Task UnlimitedRequestDoesNotOverflowItsReadSize()
    {
        using var stream = new MemoryStream("abc"u8.ToArray());
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            await FileHashCalculator.ComputeStreamAsync(stream, long.MaxValue));
    }

    [Fact]
    public async Task CancellationObservedAfterReadDoesNotReturnAHash()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new EndlessStream(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileHashCalculator.ComputeStreamAsync(stream, 10, cancellation.Token));
    }

    [Fact]
    public async Task CancelledRequestDoesNotReadTheStream()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new EndlessStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileHashCalculator.ComputeStreamAsync(stream, 10, cancellation.Token));
        Assert.Equal(0L, stream.BytesRead);
    }

    [Fact]
    public async Task NegativeBudgetIsRejected()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FileHashCalculator.ComputeStreamAsync(stream, -1));
    }

    private sealed class EndlessStream(CancellationTokenSource? cancelAfterRead = null) : Stream
    {
        public long BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span.Fill((byte)'a');
            BytesRead += buffer.Length;
            cancelAfterRead?.Cancel();
            return ValueTask.FromResult(buffer.Length);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
}
