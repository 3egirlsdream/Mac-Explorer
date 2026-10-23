using MacExplorer.Indexing;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Storage")]
[Trait("Area", "Search")]
public sealed class DiskStorageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("macexplorer-storage-").FullName;
    private string DatabasePath => Path.Combine(_folder, "index.db");

    // An actual v1 schema fixture, independent of the new schema implementation.
    internal const string LegacySearchSql = """
        CREATE TABLE search_entries (
            id INTEGER PRIMARY KEY,path TEXT NOT NULL UNIQUE COLLATE BINARY,name TEXT NOT NULL,
            parent_path TEXT NOT NULL COLLATE BINARY,extension TEXT NOT NULL,extension_key TEXT NOT NULL,
            name_key TEXT NOT NULL,initials_key TEXT NOT NULL,parent_key TEXT NOT NULL,size INTEGER NOT NULL,
            is_directory INTEGER NOT NULL,is_hidden INTEGER NOT NULL,is_symbolic_link INTEGER NOT NULL,
            created_ticks INTEGER NOT NULL,modified_ticks INTEGER NOT NULL,scan_id TEXT NOT NULL);
        CREATE INDEX search_entries_parent ON search_entries(parent_path);
        CREATE INDEX search_entries_extension ON search_entries(extension_key);
        CREATE VIRTUAL TABLE search_names USING fts5(name_key,initials_key,parent_key,
            content='search_entries',content_rowid='id',tokenize='trigram case_sensitive 1');
        CREATE TRIGGER search_entries_ai AFTER INSERT ON search_entries BEGIN
            INSERT INTO search_names(rowid,name_key,initials_key,parent_key)
                VALUES(new.id,new.name_key,new.initials_key,new.parent_key);
        END;
        INSERT INTO search_entries VALUES
            (101,'/scope/My Projects/合同2026.PDF','合同2026.PDF','/scope/My Projects','.PDF','PDF',
             '合同2026.PDF','HT2026.PDF','/SCOPE/MY PROJECTS',42,0,0,0,1,2,'original'),
            (102,'/scope/My Projects/report.txt','report.txt','/scope/My Projects','.txt','TXT',
             'REPORT.TXT','','/SCOPE/MY PROJECTS',12,0,0,0,1,2,'original');
        """;

    private static void Execute(SqliteConnection connection, string sql) => DatabaseSpaceMaintenance.Execute(connection, sql);
    private static long Count(SqliteConnection connection, string table) => DatabaseSpaceMaintenance.Scalar(connection, $"SELECT count(*) FROM {table}");

    private static void SeedProfile(SqliteConnection connection)
    {
        SqliteSchema.Initialize(connection);
        Execute(connection, """
            INSERT INTO app_settings(key,value) VALUES('KeepMe','current-config');
            CREATE TABLE personal_extension(id INTEGER PRIMARY KEY, data BLOB);
            INSERT INTO personal_extension VALUES(1,x'0001FF');
            INSERT INTO collections(name,icon,sort_order,created_at) VALUES('Saved collection','star',0,123);
            INSERT INTO collection_items(collection_id,file_path,added_at) VALUES(1,'/saved/file.pdf',123);
            INSERT INTO file_tags(file_path,tag,created_at) VALUES('/saved/file.pdf','Keep tag',123);
            """);
    }

    [Fact]
    public async Task MigrationPreservesSearchIdsAndEveryUserTable()
    {
        var factory = new DatabaseConnectionFactory(DatabasePath);
        using (var connection = factory.GetConnection())
        {
            SeedProfile(connection);
            Execute(connection, LegacySearchSql);
        }
        var catalog = new SearchCatalog(factory);
        var hit = Assert.Single(await catalog.SearchAsync("/scope", SearchQuery.Parse("ht 2026 path:\"My Projects\" ext:pdf"), new(), 10, false));
        Assert.Equal("/scope/My Projects/合同2026.PDF", hit.Path);
        using var verify = factory.GetConnection();
        Assert.Equal(2, Count(verify, "search_entries"));
        Assert.Equal(1, Count(verify, "search_directories"));
        Assert.Equal(101, DatabaseSpaceMaintenance.Scalar(verify, "SELECT id FROM search_entries WHERE extension_key='PDF'"));
        Assert.Equal(1, Count(verify, "personal_extension"));
        using var command = verify.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key='KeepMe'";
        Assert.Equal("current-config", command.ExecuteScalar());
        Assert.Equal(1, Count(verify, "collections"));
        Assert.Equal(1, Count(verify, "collection_items"));
        Assert.Equal(1, Count(verify, "file_tags"));
        Assert.Equal(2, (await new SearchCatalog(factory).SearchAsync("/scope", SearchQuery.Parse("path:projects"), new(), 10, false)).Count);
    }

    [Fact]
    public void FailedMigrationRollsBackTheOriginalSearchAndProfile()
    {
        using var connection = new DatabaseConnectionFactory(DatabasePath).GetConnection();
        SeedProfile(connection);
        Execute(connection, LegacySearchSql);
        Execute(connection, "CREATE TABLE search_directories(id INTEGER PRIMARY KEY,path TEXT,parent_key TEXT CHECK(parent_key='reject'))");
        Assert.Throws<SqliteException>(() => SearchSchema.Initialize(connection));
        Assert.Equal(2, Count(connection, "search_entries"));
        Assert.Equal(1, DatabaseSpaceMaintenance.Scalar(connection, "SELECT count(*) FROM pragma_table_info('search_entries') WHERE name='parent_path'"));
        Assert.Equal(1, Count(connection, "personal_extension"));
        Assert.Equal(1, Count(connection, "collections"));
        Assert.Equal(2, Count(connection, "search_names"));
    }

    [Fact]
    public void StartupPreparationMigratesOnceAndRestoresTheConnectionBudget()
    {
        using (var connection = new DatabaseConnectionFactory(DatabasePath).GetConnection())
        {
            SeedProfile(connection);
            Execute(connection, LegacySearchSql);
        }
        ApplicationDataMigration.PrepareSearchStorage(DatabasePath);
        ApplicationDataMigration.PrepareSearchStorage(DatabasePath);
        using var verify = DatabaseSpaceMaintenance.Open(DatabasePath);
        Execute(verify, "PRAGMA cache_size=-3000");
        SearchSchema.Initialize(verify);
        Assert.Equal(-3000, DatabaseSpaceMaintenance.Scalar(verify, "PRAGMA cache_size"));
        Assert.Equal(1, Count(verify, "search_directories"));
        Assert.Equal(2, Count(verify, "search_entries"));
        Assert.Equal(1, Count(verify, "file_tags"));
        Assert.Equal(1, Count(verify, "collection_items"));
    }

    [Fact]
    public async Task SharedParentIsStoredOnceAndPathQueriesStayExact()
    {
        var factory = new DatabaseConnectionFactory(DatabasePath);
        var catalog = new SearchCatalog(factory);
        SearchEntry Entry(string parent, int number) => new($"{parent}/report-{number}.pdf", $"report-{number}.pdf", parent,
            ".pdf", $"REPORT-{number}.PDF", "", SearchQuery.Fold(parent), 1, false, false, 0, 0, false);
        await catalog.UpsertBatchAsync(Enumerable.Range(0, 500).Select(i => Entry("/scope/a%_项目", i)).Append(Entry("/scope/aXX项目", 999)).ToArray(), "scan");
        using var connection = factory.GetConnection();
        Assert.Equal(2, Count(connection, "search_directories"));
        Assert.Equal(500, (await catalog.SearchAsync("/scope", SearchQuery.Parse("report path:a%_项目"), new(), 1000, false)).Count);
        Assert.Empty(await catalog.SearchAsync("/scope2", SearchQuery.Parse("report path:a%_项目"), new(), 10, false));
        await catalog.CompleteDirectoryAsync("/scope/a%_项目", "new-scan");
        Assert.Single(await catalog.SearchAsync("/scope", SearchQuery.Parse("report"), new(), 1000, false));
    }

    [Fact]
    public void LegacyRetirementPreservesIndependentOldProfileAndUnknownData()
    {
        var oldPath = Path.Combine(_folder, "old.db");
        using (var old = new DatabaseConnectionFactory(oldPath).GetConnection())
        {
            SeedProfile(old);
            Execute(old, LegacySearchSql);
            Execute(old, "UPDATE app_settings SET value='old-only-setting' WHERE key='KeepMe'");
        }
        using (var current = new DatabaseConnectionFactory(DatabasePath).GetConnection()) SeedProfile(current);
        SqliteConnection.ClearAllPools();
        Assert.True(ApplicationStorageMaintenance.RetireLegacyIndex(oldPath, DatabasePath));
        using var retained = DatabaseSpaceMaintenance.Open(oldPath);
        Assert.Equal(1, Count(retained, "personal_extension"));
        Assert.Equal(1, Count(retained, "collections"));
        Assert.Equal(1, Count(retained, "collection_items"));
        Assert.Equal(1, Count(retained, "file_tags"));
        Assert.Equal(0, DatabaseSpaceMaintenance.Scalar(retained, "SELECT count(*) FROM sqlite_master WHERE name='search_entries'"));
        using var read = retained.CreateCommand();
        read.CommandText = "SELECT value FROM app_settings WHERE key='KeepMe'";
        Assert.Equal("old-only-setting", read.ExecuteScalar());
        using var currentVerify = DatabaseSpaceMaintenance.Open(DatabasePath);
        using var currentRead = currentVerify.CreateCommand();
        currentRead.CommandText = read.CommandText;
        Assert.Equal("current-config", currentRead.ExecuteScalar());
    }

    [Fact]
    public void ActiveLegacyReaderOrInvalidDestinationPreventsRetirement()
    {
        var oldPath = Path.Combine(_folder, "old.db");
        using var old = new DatabaseConnectionFactory(oldPath).GetConnection();
        SeedProfile(old);
        Execute(old, LegacySearchSql);
        File.WriteAllText(DatabasePath, "invalid");
        Assert.False(ApplicationStorageMaintenance.RetireLegacyIndex(oldPath, DatabasePath));
        File.Delete(DatabasePath);
        using var current = new DatabaseConnectionFactory(DatabasePath).GetConnection();
        SeedProfile(current);
        using var transaction = old.BeginTransaction(deferred: true);
        using var read = old.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT count(*) FROM search_entries";
        Assert.Equal(2L, read.ExecuteScalar());
        Assert.False(ApplicationStorageMaintenance.RetireLegacyIndex(oldPath, DatabasePath));
        Assert.Equal(2L, read.ExecuteScalar());
        transaction.Rollback();
    }

    [Fact]
    public void ReclaimReturnsFreePagesWithoutChangingUserData()
    {
        using (var connection = new DatabaseConnectionFactory(DatabasePath).GetConnection())
        {
            SeedProfile(connection);
            Execute(connection, "CREATE TABLE disposable(bytes BLOB); INSERT INTO disposable VALUES(zeroblob(8388608)); DELETE FROM disposable;");
        }
        SqliteConnection.ClearAllPools();
        var before = new FileInfo(DatabasePath).Length;
        Assert.True(DatabaseSpaceMaintenance.Reclaim(DatabasePath, compact: true, minimumFreeBytes: 0));
        Assert.True(new FileInfo(DatabasePath).Length < before / 2);
        using var verify = DatabaseSpaceMaintenance.Open(DatabasePath);
        Assert.Equal(1, Count(verify, "personal_extension"));
        Assert.Equal(1, Count(verify, "collections"));
        Assert.Equal(0, DatabaseSpaceMaintenance.Scalar(verify, "PRAGMA freelist_count"));
    }

    [Fact]
    public void JournalRetentionIsSetOnEveryConnection()
    {
        var factory = new DatabaseConnectionFactory(DatabasePath);
        using var one = factory.GetConnection();
        using var two = factory.GetConnection();
        Assert.Equal(DatabaseSpaceMaintenance.JournalSizeLimit, DatabaseSpaceMaintenance.Scalar(one, "PRAGMA journal_size_limit"));
        Assert.Equal(DatabaseSpaceMaintenance.JournalSizeLimit, DatabaseSpaceMaintenance.Scalar(two, "PRAGMA journal_size_limit"));
    }

    [Fact]
    public void CacheCleanupPreservesRecentFilesAndRemoteEdits()
    {
        var cache = Directory.CreateDirectory(Path.Combine(_folder, "thumbnail-cache")).FullName;
        var old = Path.Combine(cache, "old.png");
        var recent = Path.Combine(cache, "recent.png");
        File.WriteAllText(old, "old"); File.WriteAllText(recent, "recent");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var edits = Directory.CreateDirectory(Path.Combine(_folder, "remote-edit")).FullName;
        File.WriteAllText(Path.Combine(edits, "unsynced.txt"), "important");
        ApplicationStorageMaintenance.CleanLegacyCaches(_folder, Path.Combine(_folder, "new-cache"), DateTime.UtcNow.AddDays(-7));
        Assert.False(File.Exists(old)); Assert.True(File.Exists(recent));
        Assert.Equal("important", File.ReadAllText(Path.Combine(edits, "unsynced.txt")));
    }

    [Fact]
    public void BusyWalIsPreservedUntilReadersFinish()
    {
        var factory = new DatabaseConnectionFactory(DatabasePath);
        using var writer = factory.GetConnection();
        Execute(writer, "CREATE TABLE journal_fixture(value TEXT); INSERT INTO journal_fixture VALUES('old')");
        using var readerConnection = factory.GetConnection();
        using var snapshot = readerConnection.BeginTransaction(deferred: true);
        using var read = readerConnection.CreateCommand();
        read.Transaction = snapshot;
        read.CommandText = "SELECT value FROM journal_fixture";
        Assert.Equal("old", read.ExecuteScalar());
        Execute(writer, "UPDATE journal_fixture SET value='new'");
        Assert.False(DatabaseSpaceMaintenance.Reclaim(DatabasePath, compact: false));
        Assert.True(File.Exists(DatabasePath + "-wal"));
        Assert.Equal("old", read.ExecuteScalar());
        snapshot.Rollback();
        Assert.True(DatabaseSpaceMaintenance.Reclaim(DatabasePath, compact: false));
        read.Transaction = null;
        Assert.Equal("new", read.ExecuteScalar());
        Assert.Equal(0, new FileInfo(DatabasePath + "-wal").Length);
    }

    [Fact]
    public void ParentDirectoryAliasCannotRetireTheActiveDatabase()
    {
        using (var connection = new DatabaseConnectionFactory(DatabasePath).GetConnection())
        {
            SeedProfile(connection);
            Execute(connection, LegacySearchSql);
        }
        SqliteConnection.ClearAllPools();
        var link = Path.Combine(_folder, "alias");
        Directory.CreateSymbolicLink(link, _folder);
        try
        {
            Assert.False(ApplicationStorageMaintenance.RetireLegacyIndex(Path.Combine(link, "index.db"), DatabasePath));
            using var verify = DatabaseSpaceMaintenance.Open(DatabasePath);
            Assert.Equal(2, Count(verify, "search_entries"));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void HardLinkCannotRetireTheActiveDatabase()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using (var connection = new DatabaseConnectionFactory(DatabasePath).GetConnection())
        {
            SeedProfile(connection);
            Execute(connection, LegacySearchSql);
        }
        SqliteConnection.ClearAllPools();
        var alias = Path.Combine(_folder, "hard-link.db");
        Assert.Equal(0, Link(DatabasePath, alias));
        Assert.False(ApplicationStorageMaintenance.RetireLegacyIndex(alias, DatabasePath));
        using var verify = DatabaseSpaceMaintenance.Open(DatabasePath);
        Assert.Equal(2, Count(verify, "search_entries"));
        Assert.Equal(1, Count(verify, "file_tags"));
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, true);
    }
}
