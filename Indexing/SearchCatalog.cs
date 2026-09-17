using System.Globalization;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

/// <summary>Independent SQLite connections, bounded concurrency and cancellation of running SQL.</summary>
public sealed class SearchCatalog
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
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE name='ai_tags'";
            _hasAiTags = command.ExecuteScalar() != null;
            return fts;
        }));
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
                return options.IsVisible(path, name, directory != 0, root);
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
            AddScopeParameters(command, root);
            for (var i = 0; i < query.NameTerms.Count; i++)
            {
                var parameter = $"@name{i}";
                command.Parameters.AddWithValue(parameter, query.NameTerms[i]);
                var nameMatch = options.UsePinyin
                    ? $"(instr(e.name_key, {parameter}) > 0 OR instr(e.initials_key, {parameter}) > 0)"
                    : $"instr(e.name_key, {parameter}) > 0";
                predicates.Add(ai
                    ? $"({nameMatch} OR EXISTS (SELECT 1 FROM ai_tags a WHERE a.file_path=e.path AND instr(search_fold(a.tag_value), {parameter}) > 0))"
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
                ? $"(SELECT DISTINCT file_path FROM ai_tags WHERE {ScopePredicate("file_path")}) tagged JOIN search_entries e ON e.path=tagged.file_path"
                : "search_entries e";
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
                results.Add(ReadEntry(reader));
            }
        }
    }, ct);

    public const string UpsertSql = """
        INSERT INTO search_entries(path,name,parent_path,extension,extension_key,name_key,initials_key,parent_key,
            size,is_directory,is_hidden,created_ticks,modified_ticks,is_symbolic_link,scan_id)
        VALUES(@path,@name,@parent,@ext,@extKey,@nameKey,@initials,@parentKey,@size,@dir,@hidden,@created,@modified,@link,@scan)
        ON CONFLICT(path) DO UPDATE SET name=excluded.name,parent_path=excluded.parent_path,
            extension=excluded.extension,extension_key=excluded.extension_key,name_key=excluded.name_key,
            initials_key=excluded.initials_key,parent_key=excluded.parent_key,size=excluded.size,
            is_directory=excluded.is_directory,is_hidden=excluded.is_hidden,created_ticks=excluded.created_ticks,
            modified_ticks=excluded.modified_ticks,is_symbolic_link=excluded.is_symbolic_link,scan_id=excluded.scan_id
        """;

    public Task UpsertBatchAsync(IReadOnlyList<SearchEntry> entries, string scanId, CancellationToken ct = default) =>
        RunAsync(true, (connection, _) =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertSql;
            foreach (var name in new[] { "@path", "@name", "@parent", "@ext", "@extKey", "@nameKey", "@initials", "@parentKey", "@scan" })
                command.Parameters.Add(name, SqliteType.Text);
            foreach (var name in new[] { "@size", "@dir", "@hidden", "@created", "@modified", "@link" })
                command.Parameters.Add(name, SqliteType.Integer);
            command.Prepare();
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                command.Parameters["@path"].Value = entry.Path;
                command.Parameters["@name"].Value = entry.Name;
                command.Parameters["@parent"].Value = entry.ParentPath;
                command.Parameters["@ext"].Value = entry.Extension;
                command.Parameters["@extKey"].Value = SearchQuery.Fold(entry.Extension.TrimStart('.'));
                command.Parameters["@nameKey"].Value = entry.NameKey;
                command.Parameters["@initials"].Value = entry.InitialsKey;
                command.Parameters["@parentKey"].Value = entry.ParentKey;
                command.Parameters["@size"].Value = entry.Size;
                command.Parameters["@dir"].Value = entry.IsDirectory ? 1 : 0;
                command.Parameters["@hidden"].Value = entry.IsHidden ? 1 : 0;
                command.Parameters["@link"].Value = entry.IsSymbolicLink ? 1 : 0;
                command.Parameters["@created"].Value = entry.CreatedTicks;
                command.Parameters["@modified"].Value = entry.ModifiedTicks;
                command.Parameters["@scan"].Value = scanId;
                command.ExecuteNonQuery();
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, ct);

    /// <summary>Called ONLY after a complete, successful directory enumeration.</summary>
    public Task CompleteDirectoryAsync(string directory, string scanId, CancellationToken ct = default) =>
        RunAsync(true, (connection, _) =>
        {
            using var transaction = connection.BeginTransaction();
            var removedDirectories = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT path FROM search_entries WHERE parent_path=@parent AND scan_id<>@scan AND is_directory=1";
                command.Parameters.AddWithValue("@parent", directory);
                command.Parameters.AddWithValue("@scan", scanId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) { ct.ThrowIfCancellationRequested(); removedDirectories.Add(reader.GetString(0)); }
            }
            foreach (var removed in removedDirectories)
            {
                using var prune = connection.CreateCommand();
                prune.Transaction = transaction;
                prune.CommandText = "DELETE FROM search_entries WHERE " + ScopePredicate("path");
                AddScopeParameters(prune, removed);
                prune.ExecuteNonQuery();
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM search_entries WHERE parent_path=@parent AND scan_id<>@scan";
                command.Parameters.AddWithValue("@parent", directory);
                command.Parameters.AddWithValue("@scan", scanId);
                command.ExecuteNonQuery();
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, ct);

    public Task<ulong> GetCheckpointAsync(string root, CancellationToken ct = default) => RunAsync(false, (connection, _) =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT checkpoint FROM search_roots WHERE path=@root";
        command.Parameters.AddWithValue("@root", root);
        return ulong.TryParse(command.ExecuteScalar()?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }, ct);

    public Task SaveCheckpointAsync(string root, ulong checkpoint, SearchIndexPhase phase, CancellationToken ct = default) =>
        RunAsync(true, (connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO search_roots(path,checkpoint,phase,updated_ticks) VALUES(@root,@checkpoint,@phase,@now)
                ON CONFLICT(path) DO UPDATE SET checkpoint=excluded.checkpoint,phase=excluded.phase,updated_ticks=excluded.updated_ticks
                """;
            command.Parameters.AddWithValue("@root", root);
            command.Parameters.AddWithValue("@checkpoint", checkpoint.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@phase", phase.ToString());
            command.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
            command.ExecuteNonQuery();
            return true;
        }, ct);

    public static string ScopePredicate(string column) =>
        $"({column}=@root COLLATE BINARY OR ({column}>=@prefix COLLATE BINARY AND {column}<@upper COLLATE BINARY))";

    private static void AddScopeParameters(SqliteCommand command, string root)
    {
        command.Parameters.AddWithValue("@root", root);
        command.Parameters.AddWithValue("@prefix", SearchPath.Prefix(root));
        command.Parameters.AddWithValue("@upper", SearchPath.UpperBound(root));
    }

    private static SearchEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
        reader.GetInt32(8) != 0, reader.GetInt32(9) != 0, reader.GetInt64(10), reader.GetInt64(11), reader.GetInt32(12) != 0);
}
