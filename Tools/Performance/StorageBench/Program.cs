using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MacExplorer.Indexing;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;

var retireLegacy = args is [_, _, "--retire-legacy"];
if (args.Length != 2 && !retireLegacy) throw new ArgumentException("Usage: StorageBench <source index.db> <new output directory> [--retire-legacy]");
var sourcePath = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new IOException("Output directory must not already exist.");
Directory.CreateDirectory(output);
var copyPath = Path.Combine(output, "profile-copy.db");
var validatorPath = Path.Combine(output, "destination-fixture.db");
var clock = Stopwatch.StartNew();
var metrics = new Dictionary<string, object>();
const string entryColumns = "id,path,name,parent_path,extension,extension_key,name_key,initials_key,parent_key,size,is_directory,is_hidden,is_symbolic_link,created_ticks,modified_ticks,scan_id";
void Progress(string phase) => Console.WriteLine($"{clock.Elapsed.TotalSeconds:F1}s {phase}");
try
{
    Progress("Creating a consistent read-only source snapshot");
    using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
           { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
    using (var copy = new SqliteConnection($"Data Source={copyPath};Pooling=False"))
    {
        source.Open(); copy.Open(); source.BackupDatabase(copy);
    }
    if (retireLegacy)
    {
        metrics["beforeBytes"] = new FileInfo(copyPath).Length;
        Dictionary<string, string> before;
        using (var snapshot = DatabaseSpaceMaintenance.Open(copyPath)) before = ProfileHashes(snapshot, excludeFileCaches: true);
        using (var validator = new SqliteConnection($"Data Source={validatorPath};Pooling=False"))
        {
            validator.Open(); SqliteSchema.Initialize(validator);
        }
        Progress("Retiring derived indexes on the legacy snapshot");
        if (!ApplicationStorageMaintenance.RetireLegacyIndex(copyPath, validatorPath))
            throw new InvalidOperationException("Legacy snapshot maintenance was deferred.");
        using (var retained = DatabaseSpaceMaintenance.Open(copyPath))
        {
            var after = ProfileHashes(retained, excludeFileCaches: true);
            if (before.Count != after.Count || before.Any(p => !after.TryGetValue(p.Key, out var hash) || p.Value != hash))
                throw new InvalidOperationException("Legacy user profile fingerprint changed.");
            using var check = retained.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidOperationException("Legacy integrity check failed.");
        }
        metrics["afterBytes"] = new FileInfo(copyPath).Length;
        metrics["persistentTablesVerified"] = before.Count;
        metrics["persistentDataIdentical"] = true;
        metrics["integrityCheck"] = "ok";
        metrics["elapsedSeconds"] = clock.Elapsed.TotalSeconds;
        var summary = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "metrics.json"), summary);
        Console.WriteLine(summary);
        return;
    }
    using var connection = DatabaseSpaceMaintenance.Open(copyPath);
    metrics["beforeBytes"] = new FileInfo(copyPath).Length;
    metrics["beforeEntries"] = DatabaseSpaceMaintenance.Scalar(connection, "SELECT count(*) FROM search_entries");
    Progress("Hashing all search records and persistent profile tables");
    var entriesBefore = HashQuery(connection, $"SELECT {entryColumns} FROM search_entries ORDER BY id");
    var profileBefore = ProfileHashes(connection);
    var queries = new List<SearchQuery>
    {
        SearchQuery.Parse("合同"), SearchQuery.Parse("report"), SearchQuery.Parse("2026 ext:pdf"),
        SearchQuery.Parse("path:Documents"), SearchQuery.Parse("ht"), SearchQuery.Parse("100%_final")
    };
    using (var samples = connection.CreateCommand())
    {
        samples.CommandText = "SELECT name_key FROM search_entries WHERE is_directory=0 AND id%8191=0 AND length(name_key)>3 LIMIT 6";
        using var rows = samples.ExecuteReader();
        while (rows.Read()) queries.Add(new([string.Concat(rows.GetString(0).EnumerateRunes().Take(4))], [], []));
    }
    var expected = queries.Select(q => LegacyQuery(connection, q)).ToArray();
    Progress("Migrating the snapshot to shared parent paths");
    var migration = Stopwatch.StartNew();
    SearchSchema.Initialize(connection);
    metrics["migrationSeconds"] = migration.Elapsed.TotalSeconds;
    Progress("Migration committed; verifying every search record");
    metrics["afterEntries"] = DatabaseSpaceMaintenance.Scalar(connection, "SELECT count(*) FROM search_entries");
    metrics["directoryRows"] = DatabaseSpaceMaintenance.Scalar(connection, "SELECT count(*) FROM search_directories");
    if (entriesBefore != HashQuery(connection, $"SELECT {entryColumns} FROM search_entry_details ORDER BY id"))
        throw new InvalidOperationException("Search record fingerprint changed.");
    Progress("Comparing search results against the original literal queries");
    var catalog = new SearchCatalog(new DatabaseConnectionFactory(copyPath));
    for (var i = 0; i < queries.Count; i++)
    {
        var actual = await catalog.SearchAsync("/", queries[i], new(false, false, false, true), 100, false);
        if (!expected[i].SequenceEqual(actual.Select(e => e.Path))) throw new InvalidOperationException($"Query {i} differs.");
        Progress($"Search comparison {i + 1}/{queries.Count} passed");
    }
    metrics["searchComparisons"] = queries.Count;
    Progress("Reclaiming free pages on the snapshot");
    DatabaseSpaceMaintenance.Compact(connection, minimumFreeBytes: 0);
    var profileAfter = ProfileHashes(connection);
    if (profileBefore.Count != profileAfter.Count || profileBefore.Any(p => !profileAfter.TryGetValue(p.Key, out var hash) || p.Value != hash))
        throw new InvalidOperationException("Persistent profile fingerprint changed.");
    using (var check = connection.CreateCommand())
    {
        check.CommandText = "PRAGMA integrity_check";
        if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidOperationException("Integrity check failed.");
    }
    metrics["persistentTablesVerified"] = profileBefore.Count;
    metrics["afterBytes"] = new FileInfo(copyPath).Length;
    metrics["elapsedSeconds"] = clock.Elapsed.TotalSeconds;
    metrics["searchRecordsIdentical"] = true;
    metrics["persistentDataIdentical"] = true;
    metrics["integrityCheck"] = "ok";
    var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(output, "metrics.json"), json);
    Console.WriteLine(json);
}
finally
{
    SqliteConnection.ClearAllPools();
    // Only files created by this invocation are removed; source is never opened for writing.
    foreach (var path in new[] { copyPath, validatorPath })
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix);
}

static string HashQuery(SqliteConnection connection, string sql)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    using var command = connection.CreateCommand(); command.CommandText = sql;
    using var rows = command.ExecuteReader();
    while (rows.Read())
    {
        var values = new object?[rows.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = rows.IsDBNull(i) ? null : rows.GetValue(i);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(values));
        hash.AppendData([10]);
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}

static Dictionary<string, string> ProfileHashes(SqliteConnection connection, bool excludeFileCaches = false)
{
    var tables = new List<string>();
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT GLOB 'search_*' ORDER BY name";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var name = rows.GetString(0);
            if (excludeFileCaches && (name is "files" or "directories" or "icon_cache" || name.StartsWith("files_fts", StringComparison.Ordinal))) continue;
            tables.Add(name);
        }
    }
    var result = new Dictionary<string, string>();
    foreach (var table in tables)
    {
        var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
        using var columns = connection.CreateCommand(); columns.CommandText = $"SELECT * FROM {quoted} LIMIT 0";
        int count; using (var reader = columns.ExecuteReader()) count = reader.FieldCount;
        result[table] = HashQuery(connection, $"SELECT * FROM {quoted} ORDER BY {string.Join(',', Enumerable.Range(1, count))}");
    }
    return result;
}

static string[] LegacyQuery(SqliteConnection connection, SearchQuery query)
{
    using var command = connection.CreateCommand();
    var filters = new List<string>();
    for (var i = 0; i < query.NameTerms.Count; i++)
    {
        filters.Add($"(instr(name_key,@name{i})>0 OR instr(initials_key,@name{i})>0)");
        command.Parameters.AddWithValue($"@name{i}", query.NameTerms[i]);
    }
    for (var i = 0; i < query.PathTerms.Count; i++)
    {
        filters.Add($"instr(parent_key,@parent{i})>0");
        command.Parameters.AddWithValue($"@parent{i}", query.PathTerms[i]);
    }
    if (query.Extensions.Count > 0)
    {
        filters.Add($"extension_key IN ({string.Join(',', query.Extensions.Select((_, i) => $"@ext{i}"))})");
        for (var i = 0; i < query.Extensions.Count; i++) command.Parameters.AddWithValue($"@ext{i}", query.Extensions[i]);
    }
    command.Parameters.AddWithValue("@exact", string.Join(' ', query.NameTerms));
    command.Parameters.AddWithValue("@first", query.NameTerms.FirstOrDefault() ?? "");
    command.CommandText = $"SELECT path FROM search_entries WHERE {string.Join(" AND ", filters)} ORDER BY CASE WHEN name_key=@exact THEN 0 WHEN @first<>'' AND instr(name_key,@first)=1 THEN 1 ELSE 2 END,name_key COLLATE BINARY,path COLLATE BINARY LIMIT 100";
    var paths = new List<string>(); using var rows = command.ExecuteReader();
    while (rows.Read()) paths.Add(rows.GetString(0));
    return paths.ToArray();
}
