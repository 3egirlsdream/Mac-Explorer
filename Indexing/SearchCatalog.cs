using System.Globalization;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

/// <summary>Independent SQLite connections, bounded concurrency and cancellation of running SQL.</summary>
public sealed partial class SearchCatalog
{
    private readonly DatabaseConnectionFactory _connections;
    private readonly Lazy<Task<bool>> _initialized;
    private readonly SemaphoreSlim _readers = new(2, 2);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private bool _hasAiTags;

    public SearchCatalog(DatabaseConnectionFactory connections)
    {
        _connections = connections;
        _initialized = new(() => Task.Run(() =>
        {
            using var connection = _connections.GetConnection();
            var fts = SearchSchema.Initialize(connection);
            using (var transaction = connection.BeginTransaction()) { FeatureUpgradeSchema.Ensure(connection, transaction); transaction.Commit(); }
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE name='ai_tags'";
            _hasAiTags = command.ExecuteScalar() != null;
            return fts;
        }));
    }

    internal async Task<IDisposable> AcquireExclusiveMaintenanceAsync(CancellationToken ct = default)
    {
        await _initialized.Value.WaitAsync(ct).ConfigureAwait(false);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        return new MaintenanceLease(_writer);
    }

    private sealed class MaintenanceLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    private async Task<T> RunAsync<T>(bool write, Func<SqliteConnection, bool, T> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fts = await _initialized.Value.WaitAsync(ct).ConfigureAwait(false);
        var gate = write ? _writer : _readers;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var connection = _connections.GetConnection();
                // sqlite3_interrupt is connection-scoped. Each operation owns its connection;
                // disposing this registration BEFORE the connection prevents use-after-close.
                using var registration = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = action(connection, fts);
                    ct.ThrowIfCancellationRequested();
                    return result;
                }
                catch (SqliteException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private const string Columns = """
        e.path, e.name, e.parent_path, e.extension, e.name_key, e.initials_key, e.parent_key,
        e.size, e.is_directory, e.is_hidden, e.created_ticks, e.modified_ticks, e.is_symbolic_link
        """;

    public Task<IReadOnlyList<SearchEntry>> SearchAsync(string root, SearchQuery query, SearchOptions options,
        int limit, bool includeAiTags, CancellationToken ct = default) => RunAsync(false, (connection, fts) =>
    {
        if (query.IsEmpty || limit <= 0) return (IReadOnlyList<SearchEntry>)Array.Empty<SearchEntry>();
        root = SearchPath.Normalize(root);
        connection.CreateFunction<string, string, long, bool>("search_visible",
            (path, name, directory) =>
            {
                ct.ThrowIfCancellationRequested();
                return Services.DirectoryAccess.Current.CanAccess(path) && options.IsVisible(path, name, directory != 0, root);
            });
        connection.CreateFunction<string, string>("search_fold", SearchQuery.Fold, isDeterministic: true);
        var results = new List<SearchEntry>();
        ReadMatches(false);
        if (includeAiTags && _hasAiTags && query.NameTerms.Count > 0 && results.Count < limit)
            ReadMatches(true);
        return (IReadOnlyList<SearchEntry>)results;

        void ReadMatches(bool ai)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            var predicates = new List<string>
            {
                ScopePredicate("e.path"),
                "search_visible(e.path, e.name, e.is_directory)"
            };
            SqlitePathScope.Bind(command, root);
            for (var i = 0; i < query.NameTerms.Count; i++)
            {
                var parameter = $"@name{i}";
                command.Parameters.AddWithValue(parameter, query.NameTerms[i]);
                var nameMatch = options.UsePinyin
                    ? $"(instr(e.name_key, {parameter}) > 0 OR instr(e.initials_key, {parameter}) > 0)"
                    : $"instr(e.name_key, {parameter}) > 0";
                predicates.Add(ai
                    ? $"({nameMatch} OR EXISTS (SELECT 1 FROM ai_tags a WHERE a.file_path=e.path AND instr(search_fold(a.tag_value), {parameter}) > 0) OR EXISTS(SELECT 1 FROM document_segments ds JOIN document_index di ON di.path=ds.path WHERE ds.path=e.path AND di.modified=e.modified_ticks AND di.size=e.size AND di.status IN ('Ready','Truncated') AND instr(search_fold(ds.text),{parameter})>0))"
                    : nameMatch);
            }
            for (var i = 0; i < query.PathTerms.Count; i++)
            {
                var parameter = $"@parent{i}";
                command.Parameters.AddWithValue(parameter, query.PathTerms[i]);
                predicates.Add($"instr(e.parent_key, {parameter}) > 0");
            }
            if (query.Extensions.Count > 0)
            {
                var parameters = query.Extensions.Select((extension, i) =>
                {
                    var parameter = $"@ext{i}";
                    command.Parameters.AddWithValue(parameter, extension);
                    return parameter;
                });
                predicates.Add($"e.extension_key IN ({string.Join(",", parameters)})");
            }
            if (!ai && fts && query.GetTrigramExpression(options.UsePinyin) is { } expression)
            {
                predicates.Add("e.id IN (SELECT rowid FROM search_names WHERE search_names MATCH @fts)");
                command.Parameters.AddWithValue("@fts", expression);
            }
            if (fts && query.GetTrigramExpression(options.UsePinyin, pathsOnly: true) is { } pathExpression)
            {
                predicates.Add("e.parent_id IN (SELECT rowid FROM search_paths WHERE search_paths MATCH @pathFts)");
                command.Parameters.AddWithValue("@pathFts", pathExpression);
            }
            if (ai && results.Count > 0)
            {
                var excluded = results.Select((entry, i) =>
                {
                    var parameter = $"@exclude{i}";
                    command.Parameters.AddWithValue(parameter, entry.Path);
                    return parameter;
                });
                predicates.Add($"e.path NOT IN ({string.Join(",", excluded)})");
            }
            command.Parameters.AddWithValue("@exact", string.Join(" ", query.NameTerms));
            command.Parameters.AddWithValue("@first", query.NameTerms.FirstOrDefault() ?? "");
            command.Parameters.AddWithValue("@limit", limit - results.Count);
            // Scope, hidden paths, literal %, _, quotes, extension, and ALL terms are
            // evaluated before LIMIT. FTS is never allowed to decide final membership.
            var source = ai
                ? $"(SELECT DISTINCT file_path FROM ai_tags WHERE {ScopePredicate("file_path")} UNION SELECT path AS file_path FROM document_index WHERE {ScopePredicate("path")}) tagged JOIN search_entry_details e ON e.path=tagged.file_path"
                : "search_entry_details e";
            command.CommandText = $"""
                SELECT {Columns} FROM {source}
                WHERE {string.Join(" AND ", predicates)}
                ORDER BY CASE WHEN e.name_key=@exact THEN 0
                              WHEN @first<>'' AND instr(e.name_key,@first)=1 THEN 1 ELSE 2 END,
                         e.name_key COLLATE BINARY, e.path COLLATE BINARY
                LIMIT @limit
                """;
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var entry = ReadEntry(reader);
                if (ai) entry = entry with { ContentMatches = DocumentIndexService.ReadMatches(connection, entry.Path, query.NameTerms.FirstOrDefault() ?? "") };
                results.Add(entry);
            }
        }
    }, ct);

    internal readonly record struct SearchRootState(ulong Checkpoint, SearchIndexPhase Phase, bool HasEntries);

    public Task<ulong> GetCheckpointAsync(string root, CancellationToken ct = default) => RunAsync(false, (connection, _) =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT checkpoint FROM search_roots WHERE path=@root";
        command.Parameters.AddWithValue("@root", root);
        return ulong.TryParse(command.ExecuteScalar()?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }, ct);

    internal Task<SearchRootState> GetRootStateAsync(string root, CancellationToken ct = default) => RunAsync(false, (connection, _) =>
    {
        ulong checkpoint = 0;
        var phase = SearchIndexPhase.Building;
        var found = false;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT checkpoint, phase FROM search_roots WHERE path=@root COLLATE BINARY";
            command.Parameters.AddWithValue("@root", root);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                found = true;
                if (!ulong.TryParse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture, out checkpoint))
                    checkpoint = 0;
                phase = ParsePhase(reader.GetString(1));
            }
        }
        if (!found || checkpoint == 0 || phase != SearchIndexPhase.Ready)
            return new SearchRootState(checkpoint, phase, false);
        using var exists = connection.CreateCommand();
        exists.CommandText = $"SELECT EXISTS(SELECT 1 FROM search_entries WHERE {SqlitePathScope.Descendants("path")} LIMIT 1)";
        SqlitePathScope.Bind(exists, root);
        var hasEntries = Convert.ToInt64(exists.ExecuteScalar()) != 0;
        return new SearchRootState(checkpoint, phase, hasEntries);
    }, ct);

    private static SearchIndexPhase ParsePhase(string value) => value switch
    {
        nameof(SearchIndexPhase.Ready) => SearchIndexPhase.Ready,
        nameof(SearchIndexPhase.Partial) => SearchIndexPhase.Partial,
        nameof(SearchIndexPhase.Unavailable) => SearchIndexPhase.Unavailable,
        _ => SearchIndexPhase.Building
    };

    public Task SaveCheckpointAsync(string root, ulong checkpoint, SearchIndexPhase phase, CancellationToken ct = default) =>
        RunAsync(true, (connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO search_roots(path,checkpoint,phase,updated_ticks) VALUES(@root,@checkpoint,@phase,@now)
                ON CONFLICT(path) DO UPDATE SET checkpoint=excluded.checkpoint,phase=excluded.phase,updated_ticks=excluded.updated_ticks
                WHERE search_roots.checkpoint IS NOT excluded.checkpoint OR search_roots.phase IS NOT excluded.phase
                """;
            command.Parameters.AddWithValue("@root", root);
            command.Parameters.AddWithValue("@checkpoint", checkpoint.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@phase", phase.ToString());
            command.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
            command.ExecuteNonQuery();
            return true;
        }, ct);

    public static string ScopePredicate(string column) => SqlitePathScope.Match(column);

    private static SearchEntry ReadEntry(SqliteDataReader reader) => ReadEntry(reader, reader.GetString(2), reader.GetString(6));

    private static SearchEntry ReadEntry(SqliteDataReader reader, string parent, string parentKey) => new(
        reader.GetString(0), reader.GetString(1), parent, reader.GetString(3),
        reader.GetString(4), reader.GetString(5), parentKey, reader.GetInt64(7),
        reader.GetInt32(8) != 0, reader.GetInt32(9) != 0, reader.GetInt64(10), reader.GetInt64(11), reader.GetInt32(12) != 0);
}
