using System.Text.Json;
using System.Threading.Channels;

namespace MacExplorer.Services.Search;

public sealed partial class SearchIndexer
{
    private readonly Channel<byte> _diagnosticRequests = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _diagnosticStop = new();
    private readonly Task _diagnosticWorker;
    private long _diagnosticVersion, _writtenVersion, _diagnosticsWrites;
    private string DiagnosticsDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configuration.DatabasePath))!, "Logs");

    private void RequestDiagnostics()
    {
        _diagnosticVersion++;
        _diagnosticRequests.Writer.TryWrite(0);
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
        var path = Path.Combine(DiagnosticsDirectory, "search-index-status.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long version;
        string json;
        lock (_gate)
        {
            version = _diagnosticVersion;
            if (version == _writtenVersion) return false;
            json = JsonSerializer.Serialize(new
            {
                UpdatedUtc = _timeProvider.GetUtcNow(), ProcessId = Environment.ProcessId,
                Statistics = GetStatistics(),
                Roots = _roots.Values.OrderBy(state => state.Root, StringComparer.Ordinal).Select(state => new
                {
                    state.Root, Phase = state.Status.Phase.ToString(), state.Status.IndexedEntries,
                    state.Status.FailedDirectories, state.Status.Error, WatcherActive = state.Watcher != null,
                    state.WatcherError, Failures = state.DirectoryErrors.Values.OrderBy(error => error.Directory, StringComparer.Ordinal).ToArray()
                }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        try
        {
            Directory.CreateDirectory(DiagnosticsDirectory);
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
