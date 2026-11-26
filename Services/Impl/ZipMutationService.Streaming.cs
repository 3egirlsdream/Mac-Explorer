using System.Runtime.InteropServices;

namespace MacExplorer.Services.Impl;

internal static partial class ZipMutationService
{
    // libzip reads the original and writes the same-directory staging file. Only
    // UpdateAsync publishes that file, after checking the original and cancellation.
    private sealed class ZipStreamingSource(string path, string staging, CancellationToken token) : IDisposable
    {
        private const int BufferSize = 64 * 1024;
        private const ulong StatSize = 4;
        private FileStream _input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
        private FileStream? _output;
        private SourceCallback? _callback;
        private SourceError _error;
        private bool _committed;

        internal Exception? Failure { get; private set; }

        internal ZipHandle OpenArchive()
        {
            ValidateZipSignature(_input);
            _callback = Execute;
            var source = CreateSource(_callback, IntPtr.Zero, IntPtr.Zero);
            if (source == IntPtr.Zero) throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
            var handle = OpenSource(source, 0, IntPtr.Zero);
            if (handle != IntPtr.Zero) return new ZipHandle(handle, this);

            FreeSource(source);
            token.ThrowIfCancellationRequested();
            if (Failure is { } failure)
                throw new IOException(LocalizationText.Get("archive.edit_write_error", failure.Message), failure);
            throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
        }

        private unsafe long Execute(IntPtr state, IntPtr data, ulong length, SourceCommand command)
        {
            try
            {
                switch (command)
                {
                    case SourceCommand.Open:
                        if (_committed)
                        {
                            _input.Dispose();
                            _input = new(staging, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
                        }
                        _input.Position = 0;
                        return 0;

                    case SourceCommand.Read:
                        token.ThrowIfCancellationRequested();
                        return _input.Read(new Span<byte>((void*)data, checked((int)length)));

                    case SourceCommand.Stat:
                        var size = _committed ? new FileInfo(staging).Length : _input.Length;
                        *(ZipStat*)data = new ZipStat { Valid = StatSize, Size = (ulong)size };
                        return sizeof(ZipStat);

                    case SourceCommand.Seek:
                        Seek(_input, data);
                        return 0;

                    case SourceCommand.Tell:
                        return _input.Position;

                    case SourceCommand.BeginWrite:
                        token.ThrowIfCancellationRequested();
                        var options = new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            BufferSize = BufferSize
                        };
                        if (OperatingSystem.IsMacOS()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        _output = new(staging, options);
                        return 0;

                    case SourceCommand.Write:
                        token.ThrowIfCancellationRequested();
                        _output!.Write(new ReadOnlySpan<byte>((void*)data, checked((int)length)));
                        return (long)length;

                    case SourceCommand.SeekWrite:
                        Seek(_output!, data);
                        return 0;

                    case SourceCommand.TellWrite:
                        return _output!.Position;

                    case SourceCommand.CommitWrite:
                        CloseOutput();
                        _committed = true;
                        return 0;

                    case SourceCommand.RollbackWrite:
                        CloseOutput();
                        return 0;

                    case SourceCommand.Error:
                        ((int*)data)[0] = (int)_error;
                        ((int*)data)[1] = 0;
                        return sizeof(int) * 2;

                    case SourceCommand.Supports:
                        // All contiguous writable commands, up to and including Remove.
                        // Deliberately omit BeginWriteCloning for this disk.
                        var writable = (1L << ((int)SourceCommand.Remove + 1)) - 1;
                        return writable | (1L << (int)SourceCommand.SupportsReopen);

                    case SourceCommand.Close:
                    case SourceCommand.Free:
                        // Managed ownership keeps the input available across reopen cycles.
                        return 0;

                    default:
                        // KeepEmptyArchive makes Remove unnecessary. Never remove the source.
                        _error = SourceError.Unsupported;
                        return -1;
                }
            }
            catch (Exception ex)
            {
                Failure ??= ex;
                _error = ex is OperationCanceledException ? SourceError.Cancelled : command switch
                {
                    SourceCommand.Open => SourceError.Open,
                    SourceCommand.Read or SourceCommand.Stat => SourceError.Read,
                    SourceCommand.Seek or SourceCommand.SeekWrite => SourceError.Seek,
                    _ => SourceError.Write
                };
                return -1;
            }
        }

        private static unsafe void Seek(Stream stream, IntPtr args)
            => stream.Seek(*(long*)args, (SeekOrigin)((int*)args)[2]);

        private void CloseOutput()
        {
            var output = _output;
            _output = null;
            output?.Dispose();
        }

        public void Dispose()
        {
            try { CloseOutput(); }
            finally { _input.Dispose(); }
        }
    }

    private enum SourceCommand
    {
        Open, Read, Close, Stat, Error, Free, Seek, Tell, BeginWrite, CommitWrite,
        RollbackWrite, Write, SeekWrite, TellWrite, Supports, Remove, SupportsReopen = 20
    }

    private enum SourceError
    {
        Seek = 4, Read = 5, Write = 6, Open = 11, Unsupported = 28, Cancelled = 32
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long SourceCallback(IntPtr state, IntPtr data, ulong length, SourceCommand command);

    [DllImport(Library, EntryPoint = "zip_source_function_create")]
    private static extern IntPtr CreateSource(SourceCallback callback, IntPtr state, IntPtr error);

    [DllImport(Library, EntryPoint = "zip_open_from_source")]
    private static extern IntPtr OpenSource(IntPtr source, int flags, IntPtr error);
}
