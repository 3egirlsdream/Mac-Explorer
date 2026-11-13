using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services.Subscriptions;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

public sealed class DocumentIndexService(DatabaseConnectionFactory database, DocumentContentExtractor extractor,
    IAiTagService tags, IBackgroundTaskManager tasks, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    internal static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(10);
    public event Action<string>? Changed;
    public async Task IndexDirectoryAsync(IReadOnlyList<FileSystemEntry> entries, CancellationToken token)
    {
        if (!FeatureAccess.CanUse(PremiumFeature.ContentSearch)) return;
        foreach (var entry in entries.Where(e => !e.IsDirectory && !e.IsVirtual && !e.IsSymbolicLink && Path.IsPathFullyQualified(e.FullPath) && DocumentContentExtractor.Supports(e.FullPath)))
        { token.ThrowIfCancellationRequested(); await IndexFileAsync(entry.FullPath, token); }
    }
    public async Task IndexFileAsync(string path, CancellationToken token = default, bool retryFailed = false)
    {
        if (!DocumentContentExtractor.Supports(path)) return;
        FeatureAccess.Require(PremiumFeature.ContentSearch);
        await _gate.WaitAsync(token);
        BackgroundTaskInfo? task = null;
        long modified = 0, size = -1;
        try
        {
            DirectoryAccess.Current.EnsureAccess(path);
            var info = new FileInfo(path); modified = info.LastWriteTimeUtc.Ticks; size = info.Length;
            using (var connection = database.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT 1 FROM document_index WHERE path=@path AND modified=@mtime AND size=@size
                    AND (status IN ('Ready','Truncated') OR (status='Failed' AND retry_after>@now AND @retry=0))
                    """;
                command.Parameters.AddWithValue("@path", path); command.Parameters.AddWithValue("@mtime", modified); command.Parameters.AddWithValue("@size", size);
                command.Parameters.AddWithValue("@now", _clock.GetUtcNow().UtcTicks); command.Parameters.AddWithValue("@retry", retryFailed ? 1 : 0);
                if (command.ExecuteScalar() != null) return;
            }
            task = tasks.AddTask(LocalizationText.Get("content.analyzing", Path.GetFileName(path)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, task.Cts.Token);
            var content = await extractor.ExtractAsync(path, linked.Token);
            linked.Token.ThrowIfCancellationRequested(); info.Refresh();
            if (!info.Exists || info.LastWriteTimeUtc.Ticks != modified || info.Length != size) throw new IOException(LocalizationText.Get("content.changed"));
            if (!content.IsTruncated && Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                await tags.SaveAnalysisResultAsync(path, info.LastWriteTime.Ticks, new ImageAnalysisResult
                { RecognizedTexts = content.Segments.Select(s => new RecognizedText { Text = s.Text, Confidence = 1, Page = s.Location.Number }).ToList() }, linked.Token);
            await Task.Run(() =>
            {
                using var connection = database.GetConnection(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM document_segments WHERE path=@path"; command.Parameters.AddWithValue("@path", path); command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO document_segments(path,ordinal,text,location) VALUES(@path,@n,@text,@location)";
                command.Parameters.Add("@n", SqliteType.Integer); command.Parameters.Add("@text", SqliteType.Text); command.Parameters.Add("@location", SqliteType.Text);
                for (var i = 0; i < content.Segments.Count; i++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    command.Parameters["@n"].Value = i; command.Parameters["@text"].Value = content.Segments[i].Text;
                    command.Parameters["@location"].Value = JsonSerializer.Serialize(content.Segments[i].Location); command.ExecuteNonQuery();
                }
                command.Parameters.Clear(); command.CommandText = "INSERT INTO document_index(path,modified,size,status) VALUES(@p,@m,@s,@status) ON CONFLICT(path) DO UPDATE SET modified=excluded.modified,size=excluded.size,status=excluded.status,error=NULL,retry_after=0";
                command.Parameters.AddWithValue("@p", path); command.Parameters.AddWithValue("@m", modified); command.Parameters.AddWithValue("@s", size); command.Parameters.AddWithValue("@status", content.IsTruncated ? "Truncated" : "Ready"); command.ExecuteNonQuery();
                info.Refresh(); linked.Token.ThrowIfCancellationRequested();
                if (!info.Exists || info.LastWriteTimeUtc.Ticks != modified || info.Length != size) throw new IOException(LocalizationText.Get("content.changed"));
                transaction.Commit();
            }, linked.Token);
            tasks.CompleteTask(task.Id); Changed?.Invoke(path);
        }
        catch (OperationCanceledException) { if (task != null) tasks.CancelTask(task.Id); throw; }
        catch (Exception ex)
        {
            if (task != null) tasks.FailTask(task.Id, ex.Message, ex.ToString());
            await Task.Run(() =>
            {
                using var connection = database.GetConnection(); using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO document_index(path,modified,size,status,error,retry_after) VALUES(@p,@m,@s,'Failed',@e,@retry)
                    ON CONFLICT(path) DO UPDATE SET modified=excluded.modified,size=excluded.size,status='Failed',error=excluded.error,retry_after=excluded.retry_after
                    """;
                command.Parameters.AddWithValue("@p", path); command.Parameters.AddWithValue("@m", modified); command.Parameters.AddWithValue("@s", size);
                command.Parameters.AddWithValue("@retry", (_clock.GetUtcNow() + FailureRetryDelay).UtcTicks);
                command.Parameters.AddWithValue("@e", ex.Message[..Math.Min(1000, ex.Message.Length)]); command.ExecuteNonQuery();
            });
        }
        finally { _gate.Release(); }
    }
    internal static IReadOnlyList<DocumentMatch> ReadMatches(SqliteConnection connection, string path, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text,location FROM document_segments WHERE path=@p ORDER BY ordinal";
        command.Parameters.AddWithValue("@p", path);
        var results = new List<DocumentMatch>(); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var text = reader.GetString(0);
            if (query.Length > 0 && !Search.SearchQuery.Fold(text).Contains(Search.SearchQuery.Fold(query), StringComparison.Ordinal)) continue;
            if (results.Count >= 20) break;
            var at = query.Length == 0 ? 0 : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, at - 80); var snippet = text.Substring(start, Math.Min(320, text.Length - start));
            if (start > 0) snippet = "…" + snippet;
            if (start + 320 < text.Length) snippet += "…";
            if (JsonSerializer.Deserialize<DocumentLocation>(reader.GetString(1)) is { } location) results.Add(new(snippet, location));
        }
        return results;
    }
}
