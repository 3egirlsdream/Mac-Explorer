using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MacExplorer.Indexing;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;

if (args.Length < 2) throw new ArgumentException("SearchWriteBench <fixture> <output> [idle-seconds] [exclude-churn] [audit]");
if (Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT") == null)
    throw new InvalidOperationException("Run through Tools/Testing/run-isolated.sh.");
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var idleSeconds = args.Length > 2 ? int.Parse(args[2]) : 0;
var database = Path.Combine(output, "index.db");
var configuration = new IndexConfiguration { DatabasePath = database };
if (args.Contains("exclude-churn")) configuration.ExcludedPaths.Add(Path.Combine(root, "churn"));
var changes = new Changes();
var catalog = new SearchCatalog(new DatabaseConnectionFactory(database));
var audit = args.Contains("audit");
if (audit)
{
    using var connection = new DatabaseConnectionFactory(database).GetConnection();
    SearchSchema.Initialize(connection);
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE bench_changes(inserted INTEGER,updated INTEGER,deleted INTEGER);
        INSERT INTO bench_changes VALUES(0,0,0);
        CREATE TRIGGER bench_insert AFTER INSERT ON search_entries BEGIN UPDATE bench_changes SET inserted=inserted+1; END;
        CREATE TRIGGER bench_update AFTER UPDATE ON search_entries BEGIN UPDATE bench_changes SET updated=updated+1; END;
        CREATE TRIGGER bench_delete AFTER DELETE ON search_entries BEGIN UPDATE bench_changes SET deleted=deleted+1; END;
        """;
    command.ExecuteNonQuery();
}
var pinyin = new Pinyin();
await using var indexer = new SearchIndexer(catalog, configuration, pinyin, changes);
var process = Process.GetCurrentProcess();
var samples = new List<object>();
using var sampling = new CancellationTokenSource();
long peakResident = 0;
var sampler = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            process.Refresh();
            Interlocked.Exchange(ref peakResident, Math.Max(Interlocked.Read(ref peakResident), process.WorkingSet64));
            await Task.Delay(500, sampling.Token);
        }
    }
    catch (OperationCanceledException) { }
});
try
{
    await Measure("initial", async () => { indexer.EnsureRoot(root); await Settled(root); });
    var flat = Path.Combine(root, "flat");
    await Measure("unchanged-50", async () =>
    {
        for (var i = 0; i < 50; i++)
        {
            changes.Emit(flat, true, true);
            await Settled(root);
        }
    });
    await Measure("single-file", async () =>
    {
        var path = Path.Combine(flat, "report-00000.txt");
        await Mutate("from pathlib import Path; import sys; Path(sys.argv[1]).write_text('changed content')", path);
        changes.Emit(path, false, false, "Modified");
        await Settled(root);
    });
    await Measure("lifecycle", async () =>
    {
        var path = Path.Combine(flat, "created-report.txt");
        await Mutate("from pathlib import Path; import sys; Path(sys.argv[1]).write_text('created')", path);
        changes.Emit(path, false, false, "Created");
        await Settled(root);
        var renamed = Path.Combine(flat, "renamed-report.txt");
        await Mutate("from pathlib import Path; import sys; Path(sys.argv[1]).rename(sys.argv[2])", path, renamed);
        changes.Emit(path, false, false, "Renamed"); changes.Emit(renamed, false, false, "Renamed");
        await Settled(root);
        await Mutate("from pathlib import Path; import sys; Path(sys.argv[1]).unlink()", renamed);
        changes.Emit(renamed, false, false, "Removed");
        await Settled(root);
    });
    await Measure("atomic-save", async () =>
    {
        var path = Path.Combine(flat, "report-00001.txt");
        await Mutate("from pathlib import Path; import os,sys; p=Path(sys.argv[1]); t=p.with_suffix('.saving'); t.write_text('atomic replacement'); os.replace(t,p)", path);
        changes.Emit(path, kind: "Renamed"); await Settled(root);
    });
    await Measure("directory-lifecycle", async () =>
    {
        var path = Path.Combine(root, "created-directory");
        var renamed = Path.Combine(root, "renamed-directory");
        await Mutate("from pathlib import Path; import sys; p=Path(sys.argv[1]); p.mkdir(); (p/'child-report.txt').write_text('child')", path);
        changes.Emit(path, true, kind: "Created"); await Settled(root);
        await Mutate("from pathlib import Path; import sys; Path(sys.argv[1]).rename(sys.argv[2])", path, renamed);
        changes.Emit(path, true, kind: "Renamed"); changes.Emit(renamed, true, kind: "Renamed"); await Settled(root);
        await Mutate("import shutil,sys; shutil.rmtree(sys.argv[1])", renamed);
        changes.Emit(renamed, true, kind: "Removed"); await Settled(root);
        if ((await catalog.SearchAsync(root, SearchQuery.Parse("child-report"), new(), 10, false)).Count != 0)
            throw new InvalidOperationException("Deleted subtree remained indexed.");
    });
    await Measure("burst-1000", async () =>
    {
        for (var i = 0; i < 1000; i++)
            changes.Emit(Path.Combine(root, "tree", $"branch-{i / 100}", $"directory-{i:0000}", "Report.txt"));
        await Settled(root);
    });
    await Measure("change-during-scan", async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pinyin.Inspect = () => { pinyin.Inspect = null; entered.TrySetResult(); };
        changes.Emit(root, true, true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var path = Path.Combine(flat, "report-00002.txt");
        await Mutate("from pathlib import Path; import sys; Path(sys.argv[1]).write_text('changed during scan')", path);
        changes.Emit(path, kind: "Modified"); await Settled(root);
        if ((await catalog.SearchAsync(flat, SearchQuery.Parse("report-00002"), new(), 10, false)).Single().Size != 19)
            throw new InvalidOperationException("Change during traversal was lost.");
    });
    await Measure("churn", async () =>
    {
        var churn = Path.Combine(root, "churn");
        await Mutate("from pathlib import Path; import sys; r=Path(sys.argv[1]); r.mkdir(exist_ok=True); [(r/str(i)).mkdir(exist_ok=True) for i in range(100)]", churn);
        changes.Emit(churn, true, false, "Created");
        await Settled(root);
        for (var batch = 0; batch < 10; batch++)
        {
            await Mutate("from pathlib import Path; import sys; r=Path(sys.argv[1]); [(r/str(i)/'log.txt').write_text(sys.argv[2]) for i in range(100)]", churn, batch.ToString());
            for (var i = 0; i < 100; i++) changes.Emit(Path.Combine(churn, i.ToString(), "log.txt"), kind: "Modified");
            await Settled(root);
        }
    });
    if (idleSeconds > 0)
    {
        await Task.Delay(TimeSpan.FromSeconds(11)); // Drain the default diagnostic tail before stable-idle measurement.
        await Measure("idle", () => Task.Delay(TimeSpan.FromSeconds(idleSeconds)));
    }
}
finally
{
    sampling.Cancel();
    await sampler;
    WriteReport();
}

async Task Settled(string directory)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    while (true)
    {
        var observation = indexer.Observe(directory);
        if (!observation.Status.IsBusy)
        {
            if (observation.Status.Phase != SearchIndexPhase.Ready) throw new IOException(observation.Status.Description);
            return;
        }
        await observation.Changed.WaitAsync(deadline.Token);
    }
}

async Task Measure(string name, Func<Task> action)
{
    var before = Native.BytesWritten();
    var cpu = process.TotalProcessorTime;
    var stats = Statistics();
    var rows = RowChanges();
    var timer = Stopwatch.StartNew();
    await action();
    // Give asynchronous filesystem accounting a chance to publish the final I/O.
    await Task.Delay(250);
    samples.Add(new { name, seconds = timer.Elapsed.TotalSeconds,
        cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
        diskWriteBytes = Native.BytesWritten() - before,
        peakResidentBytes = Interlocked.Read(ref peakResident), before = stats, after = Statistics(),
        rowsBefore = rows, rowsAfter = RowChanges() });
    WriteReport();
    Console.WriteLine(JsonSerializer.Serialize(samples[^1]));
}

object? Statistics() => typeof(SearchIndexer).GetMethod("GetStatistics", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(indexer, null);
object? RowChanges()
{
    if (!audit) return null;
    using var connection = new DatabaseConnectionFactory(database).GetConnection();
    using var command = connection.CreateCommand(); command.CommandText = "SELECT inserted,updated,deleted FROM bench_changes";
    using var reader = command.ExecuteReader(); reader.Read();
    return new { inserted = reader.GetInt64(0), updated = reader.GetInt64(1), deleted = reader.GetInt64(2) };
}
void WriteReport() => File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
{
    fixture = root, processId = Environment.ProcessId, runtime = Environment.Version.ToString(),
    excludedChurn = args.Contains("exclude-churn"), rowAudit = audit, samples
}, new JsonSerializerOptions { WriteIndented = true }));

static async Task Mutate(string script, params string[] arguments)
{
    var start = new ProcessStartInfo("python3") { UseShellExecute = false };
    start.ArgumentList.Add("-c"); start.ArgumentList.Add(script);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var child = Process.Start(start)!;
    await child.WaitForExitAsync();
    if (child.ExitCode != 0) throw new IOException("Fixture generator failed.");
}

sealed class Pinyin : IPinyinInitials
{
    internal Action? Inspect;
    public string GetInitials(string name) { Inspect?.Invoke(); return ""; }
}
sealed class Changes : ISearchChangeSource
{
    private Action<SearchChange> _publish = null!;
    private ulong _id;
    public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows)
    { _publish = onChange; return new Registration(); }
    public void Emit(string path, bool directory = false, bool rescan = false, string kind = "Unknown")
    {
        var change = new SearchChange(path, directory, rescan, ++_id);
        var property = typeof(SearchChange).GetProperty("Kind");
        if (property != null) property.SetValue(change, Enum.Parse(property.PropertyType, kind));
        _publish(change);
    }
    private sealed class Registration : IDisposable { public void Dispose() { } }
}
static class Native
{
    public static long BytesWritten()
    {
        var buffer = Marshal.AllocHGlobal(160);
        try
        {
            if (proc_pid_rusage(Environment.ProcessId, 2, buffer) != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            return Marshal.ReadInt64(buffer, 152);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern int proc_pid_rusage(int pid, int flavor, IntPtr buffer);
}
