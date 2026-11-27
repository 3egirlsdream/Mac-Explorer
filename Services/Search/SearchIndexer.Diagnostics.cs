using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace MacExplorer.Services.Search;

public sealed partial class SearchIndexer
{
    private const int MaxRecentRecoveryEvents = 8;
    private readonly Channel<byte> _diagnosticRequests = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _diagnosticStop = new();
    private readonly Task _diagnosticWorker;
    private long _diagnosticVersion, _writtenVersion, _diagnosticsWrites;

    private void RequestDiagnostics()
    {
        _diagnosticVersion++;
        _diagnosticRequests.Writer.TryWrite(0);
    }

    private void RecordRecoveryEventLocked(RootState state, string scope, uint rawFlags, ulong eventId, string disposition)
    {
        if (state.RecentRecoveries.Count >= MaxRecentRecoveryEvents)
            state.RecentRecoveries.Dequeue();
        state.RecentRecoveries.Enqueue(new SearchRecoveryEvent(
            _timeProvider.GetUtcNow(), scope, rawFlags, FormatFSEventFlags(rawFlags), eventId, disposition));
        RequestDiagnostics();
    }

    internal static string FormatFSEventFlags(uint flags)
    {
        if (flags == 0) return "None";
        var sb = new StringBuilder(64);
        void Append(uint bit, string name)
        {
            if ((flags & bit) == 0) return;
            if (sb.Length > 0) sb.Append('|');
            sb.Append(name);
        }
        Append(0x1u, "MustScanSubDirs");
        Append(0x2u, "UserDropped");
        Append(0x4u, "KernelDropped");
        Append(0x8u, "EventIdsWrapped");
        Append(0x10u, "HistoryDone");
        Append(0x20u, "RootChanged");
        Append(0x40u, "Mount");
        Append(0x80u, "Unmount");
        if (sb.Length > 0) sb.Append(" (0x").Append(flags.ToString("X")).Append(')');
        else sb.Append("0x").Append(flags.ToString("X"));
        return sb.ToString();
    }

    private async Task DiagnosticsAsync()
    {
        long? lastWrite = null;
        try
        {
            while (await _diagnosticRequests.Reader.WaitToReadAsync(_diagnosticStop.Token).ConfigureAwait(false))
            {
                while (_diagnosticRequests.Reader.TryRead(out _)) { }
                if (lastWrite is { } last)
                {
                    var remaining = _configuration.DiagnosticWriteInterval - _timeProvider.GetElapsedTime(last);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, _timeProvider, _diagnosticStop.Token).ConfigureAwait(false);
                }
                if (WriteDiagnostics()) lastWrite = _timeProvider.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (_diagnosticStop.IsCancellationRequested) { }
        finally { WriteDiagnostics(); }
    }

    private bool WriteDiagnostics()
    {
        var path = _diagnosticsStatusPath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long version;
        string json;
        lock (_gate)
        {
            version = _diagnosticVersion;
            if (version == _writtenVersion) return false;
            var roots = _roots.Values.OrderBy(state => state.Root, StringComparer.Ordinal)
                .Select(state => new SearchRootReport(
                    state.Root, state.Status.Phase.ToString(), state.Status.IndexedEntries,
                    state.Status.FailedDirectories, state.Status.Error, state.Watcher != null, state.WatcherError,
                    state.DirectoryErrors.Values.OrderBy(error => error.Directory, StringComparer.Ordinal).ToArray(),
                    state.Checkpoint, state.Deferred, state.DeferredReason?.ToString(),
                    state.CoalescedRecoveries, state.RecentRecoveries.ToArray())).ToArray();
            var report = new SearchIndexReport(_timeProvider.GetUtcNow(), Environment.ProcessId, GetStatistics(), roots);
            json = JsonSerializer.Serialize(report, SearchDiagnosticsJsonContext.Default.SearchIndexReport);
        }
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);
            File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true);
            lock (_gate) { _writtenVersion = version; _diagnosticsWrites++; }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed diagnostic write must not affect indexing. Do not retry without a state change.
            lock (_gate) _writtenVersion = version;
            return true;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

internal sealed record SearchIndexReport(DateTimeOffset UpdatedUtc, int ProcessId, SearchIndexStatistics Statistics,
    SearchRootReport[] Roots);
internal sealed record SearchRootReport(string Root, string Phase, long IndexedEntries, int FailedDirectories,
    string? Error, bool WatcherActive, string? WatcherError, SearchIndexer.DirectoryError[] Failures,
    ulong Checkpoint, bool Deferred, string? DeferredReason, long CoalescedRecoveries, SearchRecoveryEvent[] RecentRecoveries);
internal sealed record SearchRecoveryEvent(DateTimeOffset TimestampUtc, string Scope, uint RawFlags,
    string Flags, ulong EventId, string Disposition);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SearchIndexReport))]
internal sealed partial class SearchDiagnosticsJsonContext : JsonSerializerContext;
