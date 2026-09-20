using System.Reflection;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class AiAnalysisQueryPerformanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ai-query-performance-{Guid.NewGuid():N}");
    private readonly DatabaseConnectionFactory _factory;

    public AiAnalysisQueryPerformanceTests()
    {
        Directory.CreateDirectory(_root);
        _factory = new DatabaseConnectionFactory(Path.Combine(_root, "analysis.db"));
        using var connection = _factory.GetConnection();
        using var cmd = connection.CreateCommand();
        // Same status-table schema as SqliteSchema.MigrateToV6; no AI models needed.
        cmd.CommandText = """
            CREATE TABLE ai_analysis_status (
                file_path TEXT PRIMARY KEY,
                file_modified_at INTEGER NOT NULL,
                analyzed_at INTEGER NOT NULL,
                analysis_version INTEGER NOT NULL DEFAULT 1
            )
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task EmptyInput_DoesNotOpenAStatusReader()
    {
        using var service = new AiTagService(_factory);
        using var connection = _factory.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DROP TABLE ai_analysis_status";
        cmd.ExecuteNonQuery();
        Assert.Empty(await service.GetUnanalyzedFilesAsync([], []));
    }

    [Fact]
    public async Task StatusLookup_PreservesMissingChangedVersionAndDuplicateSemantics()
    {
        Seed([
            ("/photos/current.jpg", 10L, 1),
            ("/photos/changed.jpg", 10L, 1),
            ("/photos/old-version.jpg", 10L, 0),
            ("/photos/new-version.jpg", 10L, 2),
            ("/photos/Case.jpg", 10L, 1),
            ("/elsewhere/unrelated.jpg", 10L, 1)
        ]);
        using var service = new AiTagService(_factory);
        string[] paths = ["/photos/current.jpg", "/photos/changed.jpg", "/photos/old-version.jpg",
            "/photos/new-version.jpg", "/photos/missing.jpg", "/photos/missing.jpg", "/photos/case.jpg"];
        long[] times = [10, 20, 10, 10, 30, 40, 10];
        var result = await service.GetUnanalyzedFilesAsync(paths, times);
        (string, long)[] expected = [(paths[1], 20), (paths[2], 10), (paths[4], 30), (paths[5], 40), (paths[6], 10)];
        Assert.Equal(expected, result.ToArray());
    }

    [Fact]
    public async Task StatusLookup_HandlesSeveralParameterBatchesWithoutReordering()
    {
        var paths = Enumerable.Range(0, 1025).Select(i => $"/photos/{i:D4}.jpg").ToArray();
        Seed(paths.Select((path, i) => (path, i))
            .Where(item => item.i % 3 != 2)
            .Select(item => (item.path, item.i % 3 == 0 ? 10L : 9L, 1)));
        using var service = new AiTagService(_factory);
        var result = await service.GetUnanalyzedFilesAsync(paths, Enumerable.Repeat(10L, paths.Length).ToArray());
        var expected = paths.Where((_, i) => i % 3 != 0).Select(path => (path, 10L)).ToArray();
        Assert.Equal(expected, result.ToArray());
    }

    [Fact]
    public async Task StatusLookup_ParameterizesQuotesWildcardsAndUnicode()
    {
        string[] paths = ["/photos/O'Brien.jpg", "/photos/100%_done.jpg", "/照片/😀[1].jpg",
            "/photos/'); DROP TABLE ai_analysis_status;--.jpg"];
        Seed(paths.Select(path => (path, 10L, 1)));
        using var service = new AiTagService(_factory);
        Assert.Empty(await service.GetUnanalyzedFilesAsync(paths, [10, 10, 10, 10]));
        Assert.True(await service.IsFileAnalyzedAsync(paths[0], 10));
    }

    [Theory]
    [InlineData("/photos")]
    [InlineData("/photos/")]
    [InlineData("/")]
    [InlineData("/100%done")]
    [InlineData("/photo_set")]
    [InlineData("/相册/😀")]
    [InlineData("/O'Brien[2026]")]
    public async Task DirectoryLookup_UsesLiteralDirectChildBoundaries(string directory)
    {
        var prefix = directory.EndsWith('/') ? directory : directory + "/";
        string[] expected = [prefix + "One.jpg", prefix + ".hidden.jpg"];
        var all = new List<string>(expected)
        {
            prefix + "sub/nested.jpg",
            prefix + "sub/deeper/nested.jpg",
            prefix[..^1] + "0outside.jpg",
            prefix[..^1] + "-sibling/outside.jpg"
        };
        // Prefixes are exact paths, not SQL patterns or case-insensitive text.
        if (prefix != prefix.ToUpperInvariant()) all.Add(prefix.ToUpperInvariant() + "wrong-case.jpg");
        if (prefix.Contains('%')) all.Add(prefix.Replace("%", "anything") + "wrong-wildcard.jpg");
        if (prefix.Contains('_')) all.Add(prefix.Replace("_", "X") + "wrong-wildcard.jpg");
        Seed(all.Select(path => (path, 10L, 1)));
        using var service = new AiTagService(_factory);
        var result = await service.GetAnalyzedPathsInDirectoryAsync(directory);
        Assert.Equal(expected.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            result.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ConcurrentDirectoryQueries_ShareTheConnectionSafely()
    {
        Seed(Enumerable.Range(0, 20).Select(i => ($"/photos/{i}.jpg", 10L, 1)));
        using var service = new AiTagService(_factory);
        var queries = Enumerable.Range(0, 16).Select(_ => service.GetAnalyzedPathsInDirectoryAsync("/photos"));
        var results = await Task.WhenAll(queries);
        Assert.All(results, paths => Assert.Equal(20, paths.Count));
    }

    [Fact]
    public async Task MismatchedInputs_AreRejectedBeforeDatabaseWork()
    {
        using var service = new AiTagService(_factory);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetUnanalyzedFilesAsync(["/photo.jpg"], []));
    }

    [Fact]
    public async Task StatusLookup_ReadsOnlyRequestedMetadataAndRunsOffTheCallingThread()
    {
        Seed(Enumerable.Range(0, 2000).Select(i => ($"/other/{i}.jpg", 10L, 1)));
        Seed([("/photos/wanted.jpg", 10L, 1)]);
        using var service = new AiTagService(_factory);
        // A view exposes a probe over the real table without adding production hooks.
        var field = typeof(AiTagService).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var connection = Assert.IsType<SqliteConnection>(field.GetValue(service));
        var reads = 0;
        var readerThread = 0;
        connection.CreateFunction<string, long, long>("observe_status", (path, modified) =>
        {
            if (path != "/photos/wanted.jpg") throw new InvalidOperationException("Unrelated metadata was read.");
            Interlocked.Increment(ref reads);
            readerThread = Environment.CurrentManagedThreadId;
            return modified;
        });
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE ai_analysis_status RENAME TO raw_analysis_status;
                CREATE VIEW ai_analysis_status AS
                SELECT file_path, observe_status(file_path, file_modified_at) AS file_modified_at,
                       analyzed_at, analysis_version
                FROM raw_analysis_status;
                """;
            cmd.ExecuteNonQuery();
        }

        var returned = new TaskCompletionSource<Task<IReadOnlyList<(string Path, long ModifiedTicks)>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callerThread = 0;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            try { returned.SetResult(service.GetUnanalyzedFilesAsync(["/photos/wanted.jpg"], [10])); }
            catch (Exception ex) { returned.SetException(ex); }
        });
        caller.Start();
        var query = await returned.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var result = await query.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
        Assert.Empty(result);
        Assert.Equal(1, reads);
        Assert.NotEqual(callerThread, readerThread);
    }

    private void Seed(IEnumerable<(string Path, long Modified, int Version)> rows)
    {
        using var connection = _factory.GetConnection();
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO ai_analysis_status VALUES (@path, @mtime, 0, @version)";
        var path = cmd.Parameters.Add("@path", SqliteType.Text);
        var mtime = cmd.Parameters.Add("@mtime", SqliteType.Integer);
        var version = cmd.Parameters.Add("@version", SqliteType.Integer);
        foreach (var row in rows)
        {
            path.Value = row.Path;
            mtime.Value = row.Modified;
            version.Value = row.Version;
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
