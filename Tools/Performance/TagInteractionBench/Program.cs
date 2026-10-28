using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;

// Compile against the older host API; select either preserved build at runtime.
var host = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
    File.Exists(Path.Combine(host, name.Name + ".dll"))
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(host, name.Name + ".dll")) : null;
AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
    File.Exists(Path.Combine(host, "lib" + name + ".dylib"))
        ? NativeLibrary.Load(Path.Combine(host, "lib" + name + ".dylib")) : IntPtr.Zero;
await Benchmark.RunAsync(args[1]);

internal static class Benchmark
{
    public static async Task RunAsync(string output)
    {
        var root = Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")
            ?? throw new InvalidOperationException("Run with Tools/Testing/run-isolated.sh");
        var factory = new DatabaseConnectionFactory(Path.Combine(root, "tag-benchmark.db"));
        using var service = new FileTagService(factory);
        using var writer = factory.GetConnection();
        using (var seed = writer.CreateCommand())
        {
            seed.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<19)
                INSERT INTO tag_definitions(name) SELECT 'Tag-' || i FROM n;
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<99999)
                INSERT INTO file_tags(file_path,tag,is_system,created_at) SELECT '/fixture/' || i, 'Tag-' || (i%20),0,1 FROM n;
                """;
            seed.ExecuteNonQuery();
        }
        var connection = (SqliteConnection)typeof(FileTagService).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        long Writes()
        {
            using var count = connection.CreateCommand(); count.CommandText = "SELECT total_changes()";
            return (long)count.ExecuteScalar()!;
        }
        var records = new List<object>();
        async Task Measure(string name, int iteration, Func<Task> action)
        {
            var writes = Writes(); var allocated = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
            await action();
            var record = new { name, iteration, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                allocated = GC.GetTotalAllocatedBytes(true) - allocated, changedRows = Writes() - writes };
            records.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
        }
        // Warm JIT/schema. Each cold query then sees a genuine other-connection commit.
        await service.GetSidebarTagsAsync();
        for (var i = 0; i < 7; i++)
        {
            using var update = writer.CreateCommand();
            update.CommandText = "UPDATE tag_definitions SET sort_order = sort_order + 1 WHERE name = 'Tag-0'";
            update.ExecuteNonQuery();
            await Measure("definitions-invalidated", i, async () => { await service.GetSidebarTagsAsync(); });
            await Measure("definitions-100-reads", i, async () =>
            { for (var j = 0; j < 100; j++) await service.GetSidebarTagsAsync(); });
        }
        var selection = Enumerable.Range(0, 100).Select(i => "/fixture/" + i).ToArray();
        var batch = typeof(FileTagService).GetMethod("GetFileTagSelectionAsync");
        for (var i = 0; i < 3; i++)
        {
            await Measure("file-tags-100-unchanged", i, async () =>
            { foreach (var path in selection) await service.GetFileTagsAsync(path); });
            await Measure("selection-100-files", i, async () =>
            {
                if (batch != null) await (Task)batch.Invoke(service, [selection, CancellationToken.None])!;
                else
                {
                    var membership = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in selection)
                        foreach (var tag in await service.GetFileTagsAsync(path))
                            membership[tag.Name] = membership.GetValueOrDefault(tag.Name) + 1;
                    await service.GetSidebarTagsAsync();
                }
            });
        }
        var result = new { hostAssembly = typeof(FileTagService).Assembly.ManifestModule.ModuleVersionId,
            customTags = 20, relations = 100000, selection = 100, records };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
