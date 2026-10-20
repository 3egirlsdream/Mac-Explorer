using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.IO;
using SharpCompress.Providers;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;
using SharpCompress.Readers.Tar;

namespace MacExplorer.Services.Impl;

internal sealed record ArchiveReadEntry(string Key, bool IsDirectory, long? Size,
    DateTime? LastModifiedTime, bool IsEncrypted, string? LinkTarget);

/// <summary>A forward cursor: entry streams must be consumed before advancing.</summary>
internal abstract class ArchiveReadSession : IDisposable
{
    internal abstract ArchiveReadEntry Entry { get; }
    internal abstract bool MoveNext();
    internal abstract Stream OpenEntryStream();
    internal virtual void StopReading() { }
    public abstract void Dispose();

    internal static ArchiveReadSession Open(string path, ReaderOptions options, CancellationToken ct = default)
    {
        var source = new CheckpointStream(File.OpenRead(path), ct);
        try
        {
            Span<byte> header = stackalloc byte[6];
            var length = source.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            source.Position = 0;
            var compression = DetectCompression(header[..length]);
            if (compression is { } type)
            {
                bool isTar;
                var embeddedName = type == CompressionType.GZip ? ReadGzipName(source) : null;
                // Probe only a TAR header, then reopen; no decoded payload is staged on disk.
                using (var probeSource = new CheckpointStream(File.OpenRead(path), ct))
                using (var probe = Decode(probeSource, type, options))
                {
                    using var bufferedProbe = SharpCompressStream.Create(probe);
                    bufferedProbe.StartRecording();
                    var block = new byte[512];
                    var count = bufferedProbe.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);
                    bufferedProbe.Rewind();
                    // An all-zero payload also looks like an empty TAR to the library.
                    isTar = count == block.Length && (block.AsSpan().IndexOfAnyExcept((byte)0) >= 0 || ArchiveFormats.IsTarName(path))
                        && TarArchive.IsTarFile(bufferedProbe);
                }
                ct.ThrowIfCancellationRequested();
                var decoded = Decode(source, type, options);
                if (isTar)
                {
                    var buffered = SharpCompressStream.Create(decoded);
                    buffered.StartRecording();
                    try { return new ReaderSession(TarReader.OpenReader(buffered, options), source, buffered); }
                    catch { decoded.Dispose(); throw; }
                }
                return new SingleFileSession(decoded, source,
                    ArchiveFormats.SingleFileName(path, embeddedName), File.GetLastWriteTime(path));
            }

            if (!ArchiveFactory.IsArchive(source, options, out var archiveType))
                throw new InvalidFormatException("Unrecognized archive header.");
            source.Position = 0;
            if (archiveType is ArchiveType.Zip or ArchiveType.SevenZip)
            {
                // Retain the path-based API so existing split ZIP/7Z discovery still works.
                source.Dispose();
                return new RandomAccessSession(ArchiveFactory.OpenArchive(path, options));
            }
            if (archiveType == ArchiveType.Rar)
            {
                var volumes = new List<Stream>();
                try
                {
                    foreach (var part in ArchiveFactory.GetFileParts(path).Skip(1))
                    {
                        DirectoryAccess.Current.EnsureAccess(part);
                        volumes.Add(new CheckpointStream(File.OpenRead(part), ct));
                    }
                    var reader = volumes.Count == 0 ? RarReader.OpenReader(source, options)
                        : RarReader.OpenReader(new[] { source }.Concat(volumes), options);
                    return new ReaderSession(reader, source, volumes: volumes);
                }
                catch { foreach (var volume in volumes) volume.Dispose(); throw; }
            }
            return new ReaderSession(ReaderFactory.OpenReader(source, options), source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static CompressionType? DetectCompression(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 0x1f, 0x8b })) return CompressionType.GZip;
        if (header.StartsWith("BZh"u8)) return CompressionType.BZip2;
        if (header.StartsWith(new byte[] { 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00 })) return CompressionType.Xz;
        if (header.StartsWith(new byte[] { 0x28, 0xb5, 0x2f, 0xfd })
            || header.Length >= 4 && header[0] is >= 0x50 and <= 0x5f
                && header[1] == 0x2a && header[2] == 0x4d && header[3] == 0x18)
            return CompressionType.ZStandard;
        if (header.StartsWith("LZIP"u8)) return CompressionType.LZip;
        if (header.StartsWith(new byte[] { 0x1f, 0x9d })) return CompressionType.Lzw;
        return null;
    }

    private static Stream Decode(Stream source, CompressionType type, ReaderOptions options)
        => type == CompressionType.GZip
            ? new System.IO.Compression.GZipStream(source, System.IO.Compression.CompressionMode.Decompress)
            : options.Providers.CreateDecompressStream(type, source,
                CompressionContext.FromStream(source).WithReaderOptions(options));

    private static string? ReadGzipName(Stream source)
    {
        try
        {
            Span<byte> header = stackalloc byte[10];
            source.ReadExactly(header);
            if ((header[3] & 4) != 0)
            {
                Span<byte> extraLength = stackalloc byte[2];
                source.ReadExactly(extraLength);
                source.Seek(extraLength[0] | extraLength[1] << 8, SeekOrigin.Current);
            }
            if ((header[3] & 8) == 0) return null;
            var bytes = new List<byte>();
            for (var i = 0; i < 65536; i++)
            {
                var next = source.ReadByte();
                if (next < 0) throw new EndOfStreamException();
                if (next == 0)
                {
                    var name = bytes.ToArray();
                    try { return new UTF8Encoding(false, true).GetString(name); }
                    catch (DecoderFallbackException) { return Encoding.Latin1.GetString(name); }
                }
                bytes.Add((byte)next);
            }
            return null;
        }
        finally { source.Position = 0; }
    }

    private static ArchiveReadEntry Snapshot(IEntry entry) => new(
        (entry.Key ?? "").Replace('\\', '/'), entry.IsDirectory,
        entry is SharpCompress.Common.Arc.ArcEntry ? null : entry.Size,
        entry.LastModifiedTime, entry.IsEncrypted, entry.LinkTarget);

    private sealed class RandomAccessSession(IArchive archive) : ArchiveReadSession
    {
        private readonly IEnumerator<IArchiveEntry> _entries = archive.Entries.GetEnumerator();
        internal override ArchiveReadEntry Entry => Snapshot(_entries.Current);
        internal override bool MoveNext() => _entries.MoveNext();
        internal override Stream OpenEntryStream() => _entries.Current.OpenEntryStream();
        public override void Dispose() { _entries.Dispose(); archive.Dispose(); }
    }

    private sealed class ReaderSession(IReader reader, Stream source, Stream? decoded = null,
        IReadOnlyList<Stream>? volumes = null) : ArchiveReadSession
    {
        internal override ArchiveReadEntry Entry => Snapshot(reader.Entry);
        internal override bool MoveNext()
        {
            var next = reader.MoveToNextEntry();
            if (!next && decoded != null) decoded.CopyTo(Stream.Null);
            return next;
        }
        internal override Stream OpenEntryStream() => reader.OpenEntryStream();
        internal override void StopReading() => reader.Cancel();
        public override void Dispose()
        {
            // Stop early preview disposal from draining the rest of a solid archive.
            reader.Cancel();
            try { reader.Dispose(); }
            finally
            {
                try { decoded?.Dispose(); }
                finally
                {
                    source.Dispose();
                    if (volumes != null) foreach (var volume in volumes) volume.Dispose();
                }
            }
        }
    }

    private sealed class SingleFileSession(Stream decoded, Stream source, string name, DateTime modified)
        : ArchiveReadSession
    {
        private bool _started;
        internal override ArchiveReadEntry Entry { get; } = new(name, false, null, modified, false, null);
        internal override bool MoveNext() { if (_started) return false; _started = true; return true; }
        internal override Stream OpenEntryStream() => decoded;
        public override void Dispose() { try { decoded.Dispose(); } finally { source.Dispose(); } }
    }

    private sealed class CheckpointStream(Stream source, CancellationToken ct) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            FileTaskContext.Checkpoint();
            return source.Read(buffer);
        }
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }
}
