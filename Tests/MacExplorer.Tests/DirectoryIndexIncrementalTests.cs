using System.Diagnostics;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class DirectoryIndexIncrementalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fkfinder-index-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteFileIndex _index;
    private readonly SqliteConnection _db;
    private readonly DatabaseConnectionFactory _factory;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public DirectoryIndexIncrementalTests()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "index.db");
        _factory = new DatabaseConnectionFactory(path);
        _index = new SqliteFileIndex(path, _factory);
        _db = _factory.GetConnection();
        Execute("""
            CREATE TABLE audit(kind TEXT, path TEXT);
            CREATE TRIGGER audit_insert AFTER INSERT ON files BEGIN INSERT INTO audit VALUES('insert',new.path); END;
            CREATE TRIGGER audit_update AFTER UPDATE ON files BEGIN INSERT INTO audit VALUES('update',new.path); END;
            CREATE TRIGGER audit_delete AFTER DELETE ON files BEGIN INSERT INTO audit VALUES('delete',old.path); END;
            CREATE TABLE fts_audit(value INTEGER);
            CREATE TRIGGER audit_fts AFTER INSERT ON files_fts_data BEGIN INSERT INTO fts_audit VALUES(1); END;
            """);
    }

    private static FileSystemEntry Entry(string path, long size = 1, string? name = null, bool hidden = false) => new()
    {
        FullPath = path, Name = name ?? Path.GetFileName(path), Extension = Path.GetExtension(path),
        Size = size, Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LastModified = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), IsHidden = hidden
    };

    [Fact]
    public async Task UnchangedSnapshotDoesNotWriteFilesOrFtsAndPreservesRowIdsAndIndexedAt()
    {
        FileSystemEntry[] entries = [Entry("/data/a.txt"), Entry("/data/B.txt")];
        await _index.UpdateDirectoryAsync("/data", entries, Ct);
        Assert.Equal(2, Scalar("SELECT count(*) FROM audit WHERE kind='insert'"));
        var before = Rows("SELECT path||':'||rowid||':'||indexed_at FROM files ORDER BY path");
        Execute("DELETE FROM audit; DELETE FROM fts_audit; UPDATE directories SET last_scanned=0;");
        await _index.UpdateDirectoryAsync("/data", entries.Reverse().ToArray(), Ct);
        Assert.Equal(before, Rows("SELECT path||':'||rowid||':'||indexed_at FROM files ORDER BY path"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM fts_audit"));
        Assert.True(await _index.IsDirectoryFreshAsync("/data", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task DeltaUpdatesOnlyChangedRowsAndKeepsOtherDirectories()
    {
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/a.txt"), Entry("/data/b.txt"), Entry("/data/c.txt")], Ct);
        await _index.UpdateDirectoryAsync("/other", [Entry("/other/keep.txt")], Ct);
        Execute("DELETE FROM audit; DELETE FROM fts_audit;");
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/a.txt", 42), Entry("/data/c.txt"), Entry("/data/renamed.txt")], Ct);
        Assert.Equal(["delete:/data/b.txt", "insert:/data/renamed.txt", "update:/data/a.txt"],
            Rows("SELECT kind||':'||path FROM audit ORDER BY kind,path"));
        Assert.Equal(42, (await _index.GetEntryAsync("/data/a.txt"))!.Size);
        Assert.NotNull(await _index.GetEntryAsync("/other/keep.txt"));
        Assert.Empty(await _index.SearchByNameAsync("b.txt"));
        Assert.Single(await _index.SearchByNameAsync("renamed"));
        Assert.True(Scalar("SELECT count(*) FROM fts_audit") > 0);
    }

    [Fact]
    public async Task MetadataOnlyUpdateDoesNotTouchFtsButNameChangeDoes()
    {
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/a.txt")], Ct);
        Execute("DELETE FROM audit; DELETE FROM fts_audit;");
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/a.txt", 72, hidden: true)], Ct);
        Assert.Equal(1, Scalar("SELECT count(*) FROM audit WHERE kind='update'"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM fts_audit"));
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/a.txt", 72, "searchable", true)], Ct);
        Assert.Single(await _index.SearchByNameAsync("searchable"));
        Assert.True(Scalar("SELECT count(*) FROM fts_audit") > 0);
    }

    [Fact]
    public async Task AllPersistedAttributesAreComparedWithSqlNullSemantics()
    {
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/plain")], Ct);
        Execute("UPDATE files SET extension='', content_type='stale', created_at=1, modified_at=2, is_directory=1; DELETE FROM audit;");
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/plain")], Ct);
        Assert.Equal(1, Scalar("SELECT count(*) FROM audit WHERE kind='update'"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM files WHERE extension IS NULL AND content_type IS NULL AND is_directory=0 AND created_at>1 AND modified_at>2"));
    }

    [Fact]
    public async Task EmptySnapshotClearsOnlyItsDirectoryAndFts()
    {
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/gone.txt")], Ct);
        await _index.UpdateDirectoryAsync("/other", [Entry("/other/keep.txt")], Ct);
        await _index.UpdateDirectoryAsync("/data", [], Ct);
        Assert.Empty(await _index.GetDirectoryContentsAsync("/data"));
        Assert.Empty(await _index.SearchByNameAsync("gone"));
        Assert.Single(await _index.GetDirectoryContentsAsync("/other"));
        Assert.Equal(0, Scalar("SELECT file_count FROM directories WHERE path='/data'"));
    }

    [Fact]
    public async Task MergedApplicationRowsAreReusedWithoutRemovingOtherSystemApps()
    {
        await _index.UpdateDirectoryAsync("/System/Applications", [Entry("/System/Applications/A.app"), Entry("/System/Applications/B.app")], Ct);
        await _index.UpdateDirectoryAsync("/Applications", [Entry("/Applications/A.app"), Entry("/System/Applications/B.app")], Ct);
        Execute("DELETE FROM audit;");
        await _index.UpdateDirectoryAsync("/Applications", [Entry("/Applications/A.app"), Entry("/System/Applications/B.app")], Ct);
        Assert.Equal(0, Scalar("SELECT count(*) FROM audit"));
        Assert.NotNull(await _index.GetEntryAsync("/System/Applications/A.app"));
    }

    [Fact]
    public async Task PathsAreCaseSensitiveAndFailureRollsBackPriorMutations()
    {
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/a"), Entry("/data/A")], Ct);
        Assert.Equal(2, (await _index.GetDirectoryContentsAsync("/data")).Count);
        Execute("DELETE FROM audit;");
        await Assert.ThrowsAsync<SqliteException>(() => _index.UpdateDirectoryAsync("/data",
            [Entry("/data/new"), Entry("/data/new")], Ct));
        Assert.Equal(["/data/A", "/data/a"], Rows("SELECT path FROM files ORDER BY path"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM audit"));
    }

    [Fact]
    public async Task CancellationAfterWritesRollsBackFilesFtsAndDirectoryMetadata()
    {
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/old")], Ct);
        var lastScan = Scalar("SELECT last_scanned FROM directories WHERE path='/data'");
        Execute("DELETE FROM audit; DELETE FROM fts_audit;");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var writer = (SqliteConnection)typeof(SqliteFileIndex)
            .GetField("_writeConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_index)!;
        writer.CreateFunction("cancel_index_write", () =>
        {
            cancelled.Cancel();
            return 0;
        });
        Execute("""
            CREATE TRIGGER cancel_after_directory_write AFTER UPDATE ON directories
            BEGIN SELECT cancel_index_write(); END;
            """);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _index.UpdateDirectoryAsync("/data", [Entry("/data/new")], cancelled.Token));
        Assert.Single(await _index.SearchByNameAsync("old"));
        Assert.Empty(await _index.SearchByNameAsync("new"));
        Assert.Equal(lastScan, Scalar("SELECT last_scanned FROM directories WHERE path='/data'"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM fts_audit"));
    }

    [Fact]
    public async Task V9MigrationPreservesSearchAndInstallsConditionalTriggerIdempotently()
    {
        await _index.UpdateDirectoryAsync("/data", [Entry("/data/migration.txt")], Ct);
        Execute("""
            DELETE FROM schema_version; INSERT INTO schema_version VALUES(9);
            DROP TRIGGER files_au;
            CREATE TRIGGER files_au AFTER UPDATE ON files BEGIN
                INSERT INTO files_fts(files_fts,rowid,name,path) VALUES('delete',old.rowid,old.name,old.path);
                INSERT INTO files_fts(rowid,name,path) VALUES(new.rowid,new.name,new.path);
            END;
            DELETE FROM fts_audit;
            """);
        SqliteSchema.Initialize(_db);
        SqliteSchema.Initialize(_db);
        Assert.Equal(10, Scalar("SELECT MAX(version) FROM schema_version"));
        Assert.Single(await _index.SearchByNameAsync("migration"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM fts_audit"));
        Execute("UPDATE files SET size=100;");
        Assert.Equal(0, Scalar("SELECT count(*) FROM fts_audit"));
        await _index.RenameEntryAsync("/data/migration.txt", "/data/renamed.txt", "renamed.txt");
        Assert.Empty(await _index.SearchByNameAsync("migration"));
        Assert.Single(await _index.SearchByNameAsync("renamed"));
    }

    [AvaloniaFact]
    public async Task RecordingVisitReturnsToUiWhileAnotherConnectionHoldsWriteLock()
    {
        using var service = new FrequentFolderService(_factory, "/home");
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(() =>
        {
            using var connection = _factory.GetConnection();
            using var transaction = connection.BeginTransaction();
            ready.SetResult();
            release.Wait(TimeSpan.FromSeconds(3));
            transaction.Commit();
        });
        await ready.Task.WaitAsync(Ct);
        Task record;
        var started = Stopwatch.GetTimestamp();
        try
        {
            record = service.RecordVisitAsync("/home/projects");
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
            await Dispatcher.UIThread.InvokeAsync(() => Assert.True(Dispatcher.UIThread.CheckAccess()));
        }
        finally { release.Set(); }
        await blocker;
        await record.WaitAsync(Ct);
        Assert.Equal(1, Scalar("SELECT visit_count FROM frequent_folders WHERE path='/home/projects'"));
    }

    private void Execute(string sql) { using var cmd = _db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private long Scalar(string sql) { using var cmd = _db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private string[] Rows(string sql)
    {
        using var cmd = _db.CreateCommand(); cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader(); var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }
    public void Dispose() { _db.Dispose(); _index.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
