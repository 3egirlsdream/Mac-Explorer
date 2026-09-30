using System.Globalization;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

public sealed partial class SearchCatalog
{
    public Task<IReadOnlyList<string>> GetAnalysisFieldsAsync(CancellationToken ct = default) =>
        RunAsync(false, (connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT tag_type FROM ai_tags ORDER BY tag_type";
            var fields = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) fields.Add(reader.GetString(0));
            return (IReadOnlyList<string>)fields;
        }, ct);

    /// <summary>Query analysis independently of the evictable files cache and search index coverage.
    /// Returns metadata and match sources, never OCR/PDF excerpts or face feature prints.</summary>
    public Task<IndexedFileSearchResult> SearchResourcesAsync(string root, IndexedFileQuery query,
        SearchOptions options, CancellationToken ct = default) => RunAsync(false, (connection, _) =>
    {
        root = SearchPath.Normalize(RuntimePaths.ResolveSearchRoot(root));
        Validate(query);
        var observed = new Dictionary<string, FileSystemInfo?>(StringComparer.Ordinal);
        FileSystemInfo? Observe(string path)
        {
            if (observed.TryGetValue(path, out var existing)) return existing;
            ct.ThrowIfCancellationRequested();
            FileSystemInfo? info = null;
            try
            {
                info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                info.Refresh();
                if (!info.Exists) info = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            observed[path] = info;
            return info;
        }
        connection.CreateFunction<string, bool>("resource_present", path => Observe(path) != null);
        connection.CreateFunction<string, long, bool>("resource_current", (path, ticks) =>
            Observe(path) is { } info && info.LastWriteTime.Ticks == ticks);
        connection.CreateFunction<string, string>("resource_name", Path.GetFileName, isDeterministic: true);
        connection.CreateFunction<string, string>("resource_extension", Path.GetExtension, isDeterministic: true);
        connection.CreateFunction<string, long>("resource_created", path => Observe(path)?.CreationTimeUtc.Ticks ?? 0);
        connection.CreateFunction<string, long>("resource_modified", path => Observe(path)?.LastWriteTimeUtc.Ticks ?? 0);
        connection.CreateFunction<string, long>("resource_size", path => Observe(path) is FileInfo info ? info.Length : 0);
        connection.CreateFunction<string, bool>("resource_directory", path => Observe(path) is DirectoryInfo);
        connection.CreateFunction<string, string>("resource_fold", SearchQuery.Fold, isDeterministic: true);
        connection.CreateFunction<string, string, long, bool>("resource_visible", (path, name, directory) =>
            options.IsVisible(path, name, directory != 0, root));

        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        AddScopeParameters(command, root);
        var filters = new List<string> { "resource_visible(p.path,p.name,p.is_directory)" };
        string Parameter(object value)
        {
            var name = "@filter" + command.Parameters.Count;
            command.Parameters.AddWithValue(name, value);
            return name;
        }
        string Contains(string column, string value) => $"instr(resource_fold({column}),{Parameter(SearchQuery.Fold(value))})>0";
        const string current = "EXISTS(SELECT 1 FROM ai_analysis_status s WHERE s.file_path=p.path AND s.analysis_version>=1 AND resource_current(s.file_path,s.file_modified_at))";
        string TagMatch(string predicate) => $"({current} AND EXISTS(SELECT 1 FROM ai_tags a WHERE a.file_path=p.path AND {predicate}))";
        var textPredicate = string.IsNullOrEmpty(query.Query) ? "1" : Contains("a.tag_value", query.Query);
        var namePredicate = string.IsNullOrEmpty(query.Query) ? "0" : Contains("p.name", query.Query);
        var personPredicate = string.IsNullOrEmpty(query.Query) ? "0" : Contains("c.display_name", query.Query);
        string PersonMatch(string predicate) => $"({current} AND EXISTS(SELECT 1 FROM face_observations o JOIN face_clusters c ON c.id=o.cluster_id WHERE o.file_path=p.path AND {predicate}))";
        var fileTagPredicate = string.IsNullOrEmpty(query.Query) ? "0" :
            $"EXISTS(SELECT 1 FROM file_tags t WHERE t.file_path=p.path AND {Contains("t.tag", query.Query)})";
        if (!string.IsNullOrEmpty(query.Query))
            filters.Add(query.Source switch
            {
                "name" => namePredicate,
                "content" => TagMatch($"a.tag_type='text' AND {textPredicate}"),
                "analysis" => $"({TagMatch(textPredicate)} OR {PersonMatch(personPredicate)})",
                _ => $"({namePredicate} OR {TagMatch(textPredicate)} OR {PersonMatch(personPredicate)} OR {fileTagPredicate})"
            });
        if (query.Extensions.Length > 0)
            filters.Add($"resource_fold(p.extension) IN ({string.Join(",", query.Extensions.Select(e => Parameter(SearchQuery.Fold("." + e.TrimStart('.')))))})");
        foreach (var tag in query.Tags)
            filters.Add(TagMatch($"a.tag_type={Parameter(tag.Type)} AND {Contains("a.tag_value", tag.Value)}"));
        if (query.FileTag != null)
            filters.Add($"EXISTS(SELECT 1 FROM file_tags t WHERE t.file_path=p.path AND {Contains("t.tag", query.FileTag)})");
        if (query.Person != null) filters.Add(PersonMatch(Contains("c.display_name", query.Person)));
        if (query.MinRating != null) filters.Add($"p.rating>={Parameter(query.MinRating.Value)}");
        if (query.MinSize != null) filters.Add($"p.size>={Parameter(query.MinSize.Value)}");
        if (query.MaxSize != null) filters.Add($"p.size<={Parameter(query.MaxSize.Value)}");
        AddTime("p.created_ticks", query.CreatedFrom, query.CreatedTo);
        AddTime("p.modified_ticks", query.ModifiedFrom, query.ModifiedTo);
        if (query.TakenFrom != null || query.TakenTo != null)
        {
            var dates = new List<string> { "a.tag_type='date_day'" };
            if (query.TakenFrom != null) dates.Add($"a.tag_value>={Parameter(query.TakenFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
            if (query.TakenTo != null) dates.Add($"a.tag_value<{Parameter(query.TakenTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
            filters.Add(TagMatch(string.Join(" AND ", dates)));
        }
        command.Parameters.AddWithValue("@offset", query.Offset);
        filters.Add("resource_present(p.path)");
        (string Table, string Path)[] pathSources =
        {
            ("search_entries", "path"), ("files", "path"), ("ai_analysis_status", "file_path"),
            ("file_tags", "file_path"), ("file_ratings", "file_path")
        };
        // Content-only and AI-filtered queries need no enumeration of millions of
        // filename rows. Their candidate paths already live in the analysis PK.
        if (query.Tags.Length > 0 || query.Person != null || query.TakenFrom != null || query.TakenTo != null
            || (!string.IsNullOrEmpty(query.Query) && query.Source is "content" or "analysis"))
            pathSources = [("ai_analysis_status", "file_path")];
        var paths = string.Join(" UNION ", pathSources.Select(item =>
            $"SELECT {item.Path} AS path FROM {item.Table} WHERE {ScopePredicate(item.Path)}"));
        command.CommandText = $"""
            WITH paths AS ({paths}), p AS (
                SELECT paths.path, coalesce(e.name,f.name,resource_name(paths.path)) AS name,
                    coalesce(e.extension,f.extension,resource_extension(paths.path)) AS extension,
                    coalesce(e.size,f.size,resource_size(paths.path)) AS size,
                    coalesce(e.is_directory,f.is_directory,resource_directory(paths.path)) AS is_directory,
                    coalesce(e.created_ticks,f.created_at,resource_created(paths.path)) AS created_ticks,
                    coalesce(e.modified_ticks,f.modified_at,resource_modified(paths.path)) AS modified_ticks,
                    coalesce(r.rating,0) AS rating
                FROM paths LEFT JOIN search_entries e ON e.path=paths.path
                    LEFT JOIN files f ON f.path=paths.path LEFT JOIN file_ratings r ON r.file_path=paths.path)
            SELECT p.path,p.name,p.extension,p.size,p.is_directory,p.created_ticks,p.modified_ticks,p.rating,
                {current}, {namePredicate},
                CASE WHEN {current} THEN (SELECT group_concat(DISTINCT a.tag_type) FROM ai_tags a
                    WHERE a.file_path=p.path AND {textPredicate}
                    {(query.Source == "content" ? "AND a.tag_type='text'" : "")}) END,
                {PersonMatch(personPredicate)}, {fileTagPredicate}
            FROM p WHERE {string.Join(" AND ", filters)} ORDER BY p.path COLLATE BINARY LIMIT 101 OFFSET @offset
            """;
        var matches = new List<IndexedFileMatch>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var sources = new List<string>();
                if (reader.GetBoolean(9) && query.Source is "all" or "name") sources.Add("name");
                if (!reader.IsDBNull(10) && query.Source != "name") sources.AddRange(reader.GetString(10).Split(','));
                sources.AddRange(query.Tags.Select(t => t.Type));
                if (query.FileTag != null) sources.Add("file-tag");
                if (query.Person != null) sources.Add("person");
                if (reader.GetBoolean(11) && query.Source is "all" or "analysis") sources.Add("person");
                if (reader.GetBoolean(12) && query.Source == "all") sources.Add("file-tag");
                if (query.MinRating != null) sources.Add("rating");
                if (query.Extensions.Length > 0) sources.Add("extension");
                if (query.MinSize != null || query.MaxSize != null) sources.Add("size");
                if (query.CreatedFrom != null || query.CreatedTo != null) sources.Add("created");
                if (query.ModifiedFrom != null || query.ModifiedTo != null) sources.Add("modified");
                if (query.TakenFrom != null || query.TakenTo != null) sources.Add("date_day");
                matches.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    reader.GetBoolean(4), new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                    new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero), reader.GetBoolean(8), reader.GetInt32(7),
                    sources.Distinct().ToArray()));
            }
        using var coverage = connection.CreateCommand();
        AddScopeParameters(coverage, root);
        coverage.CommandText = "SELECT count(*) FROM ai_analysis_status WHERE " + ScopePredicate("file_path");
        var analyzed = (long)coverage.ExecuteScalar()!;
        var statuses = new List<string>();
        coverage.CommandText = "SELECT path,phase FROM search_roots WHERE " + ScopePredicate("path") + " OR @root=path OR (@root>=path||'/' AND @root<path||'0')";
        using (var reader = coverage.ExecuteReader())
            while (reader.Read()) statuses.Add($"{reader.GetString(0)}: {reader.GetString(1)}");
        if (statuses.Count == 0) statuses.Add("Unindexed: 文件搜索索引尚未覆盖此范围，仍查询已有分析与元数据。");
        return new IndexedFileSearchResult(matches.Take(100).ToArray(), query.Offset,
            matches.Count > 100 ? query.Offset + 100 : null, analyzed, statuses);

        void AddTime(string column, DateTimeOffset? from, DateTimeOffset? to)
        {
            if (from != null) filters.Add($"{column}>={Parameter(from.Value.UtcTicks)}");
            if (to != null) filters.Add($"{column}<{Parameter(to.Value.UtcTicks)}");
        }
    }, ct);

    private static void Validate(IndexedFileQuery query)
    {
        if (query.Query == null || query.Extensions == null || query.Tags == null
            || query.Tags.Any(t => t == null || string.IsNullOrWhiteSpace(t.Type) || t.Value == null))
            throw new ArgumentException("query、extensions 和 tags 不能为空；tag 需指定 type 与 value。");
        if (query.Source is not ("all" or "name" or "content" or "analysis"))
            throw new ArgumentException("source 必须为 all、name、content 或 analysis。");
        if (query.Offset < 0 || query.MinRating is < 0 or > 5 || query.MinSize < 0 || query.MaxSize < 0
            || query.MinSize > query.MaxSize || query.CreatedFrom >= query.CreatedTo
            || query.ModifiedFrom >= query.ModifiedTo || query.TakenFrom >= query.TakenTo)
            throw new ArgumentException("offset、大小、评分或日期范围无效；日期上限不包含在范围内。");
        if (string.IsNullOrWhiteSpace(query.Query) && query.Extensions.Length + query.Tags.Length == 0
            && query.FileTag == null && query.Person == null && query.MinRating == null
            && query.MinSize == null && query.MaxSize == null && query.CreatedFrom == null
            && query.CreatedTo == null && query.ModifiedFrom == null && query.ModifiedTo == null
            && query.TakenFrom == null && query.TakenTo == null)
            throw new ArgumentException("请指定关键词或至少一个筛选条件。");
    }
}
