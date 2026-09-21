using Avalonia.Media.Imaging;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Controls;

/// <summary>One dashboard's decoded thumbnails, shared with its folder sheet. UI-thread owned.</summary>
public sealed class HomeThumbnailCache : IDisposable
{
    private static readonly SemaphoreSlim LoadGate = new(3);
    private readonly Dictionary<Key, Entry> _entries = [];
    private bool _disposed;
    private long _clock;
    private readonly record struct Key(string Path, long Size, DateTime Modified, string? Source, int Pixels)
    {
        public static Key For(FileSystemEntry entry, int pixels)
            => new(entry.FullPath, entry.Size, entry.LastModified, entry.ThumbnailUrl ?? entry.IconUrl, pixels);
    }
    private sealed class Entry
    {
        public Bitmap? Bitmap;
        public Task? Loading;
        public readonly CancellationTokenSource Cancellation = new();
        public int Users;
        public long Used;
    }
    public sealed class Lease : IDisposable
    {
        private Action? _release;
        public Bitmap? Bitmap { get; }
        internal Lease(Bitmap? bitmap, Action release) => (Bitmap, _release) = (bitmap, release);
        public void Dispose() { var release = _release; _release = null; release?.Invoke(); }
    }

    public Lease? TryAcquire(FileSystemEntry file, int pixels)
    {
        if (_disposed || !_entries.TryGetValue(Key.For(file, pixels), out var entry) || entry.Loading?.IsCompleted != true) return null;
        entry.Users++; entry.Used = ++_clock;
        return new Lease(entry.Bitmap, () => Release(entry));
    }

    public async Task<Lease> AcquireAsync(FileSystemEntry file, int pixels,
        Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>> provider, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = Key.For(file, pixels);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _entries.Add(key, entry);
        }
        entry.Users++; entry.Used = ++_clock;
        entry.Loading ??= LoadAsync(entry, file, pixels, provider);
        try
        {
            await entry.Loading.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return new Lease(entry.Bitmap, () => Release(entry));
        }
        catch { Release(entry); throw; }
    }

    private static async Task LoadAsync(Entry entry, FileSystemEntry file, int pixels,
        Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>> provider)
    {
        var token = entry.Cancellation.Token;
        Bitmap? bitmap = null;
        try
        {
            await Task.Delay(80, token);
            await LoadGate.WaitAsync(token);
            try
            {
                bitmap = await Task.Run(async () =>
                {
                    var result = await provider(file, pixels, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (result?.Bytes is not { Length: > 0 } bytes) return null;
                    using var stream = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(stream, pixels);
                }, token);
            }
            finally { LoadGate.Release(); }
            token.ThrowIfCancellationRequested();
            entry.Bitmap = bitmap; bitmap = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* Unsupported or unavailable files retain the normal file icon. */ }
        finally { bitmap?.Dispose(); if (token.IsCancellationRequested) entry.Cancellation.Dispose(); }
    }

    private void Release(Entry entry)
    {
        entry.Users--;
        if (entry.Users == 0 && (entry.Loading?.IsCompleted != true || _disposed)) Remove(entry);
        while (_entries.Count > 128)
        {
            var oldest = _entries.Values.Where(e => e.Users == 0).MinBy(e => e.Used);
            if (oldest == null) break;
            Remove(oldest);
        }
    }

    private void Remove(Entry entry)
    {
        var key = _entries.FirstOrDefault(pair => ReferenceEquals(pair.Value, entry)).Key;
        _entries.Remove(key);
        entry.Cancellation.Cancel();
        entry.Bitmap?.Dispose(); entry.Bitmap = null;
        // The pending loader still owns its token until it has unwound.
        if (entry.Loading?.IsCompleted == true) entry.Cancellation.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values.Where(e => e.Users == 0).ToArray()) Remove(entry);
    }
}
