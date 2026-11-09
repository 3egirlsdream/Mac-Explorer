using System.Diagnostics.Tracing;

// Test-only counters: retain numeric code-page identities in memory, never payload
// strings, object contents, native addresses, or paths in the diagnostic output.
internal sealed class RetentionJitListener : EventListener
{
    private static long _bytes, _loads;
    private static readonly HashSet<ulong> Pages = [];
    private static readonly object Sync = new();

    internal static object Statistics
    {
        get
        {
            lock (Sync) return new { loadedCodeBytes = _bytes, methodLoads = _loads,
                codePageBytes = (long)Pages.Count * Environment.SystemPageSize };
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventName?.StartsWith("MethodLoadVerbose", StringComparison.Ordinal) != true
            || data.PayloadNames == null || data.Payload == null) return;
        var sizeIndex = data.PayloadNames.IndexOf("MethodSize");
        var startIndex = data.PayloadNames.IndexOf("MethodStartAddress");
        if (sizeIndex < 0 || startIndex < 0) return;
        var size = Convert.ToUInt64(data.Payload[sizeIndex]);
        var start = Convert.ToUInt64(data.Payload[startIndex]);
        var pageSize = (ulong)Environment.SystemPageSize;
        lock (Sync)
        {
            _bytes += (long)size;
            _loads++;
            for (var page = start / pageSize * pageSize; page < start + size; page += pageSize) Pages.Add(page);
        }
    }
}
