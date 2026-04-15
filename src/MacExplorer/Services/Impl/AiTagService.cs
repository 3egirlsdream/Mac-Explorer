using MacExplorer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class AiTagService : IAiTagService
{
    private const int CurrentAnalysisVersion = 1;
    private readonly SqliteConnection _connection;
    private readonly ILogger<AiTagService>? _logger;
    private bool _disposed;

    public AiTagService(DatabaseConnectionFactory connectionFactory, ILoggerFactory? loggerFactory = null)
    {
        _connection = connectionFactory.GetConnection();
        _logger = loggerFactory?.CreateLogger<AiTagService>();
    }

    // ── Analysis status ──

    public async Task<bool> IsFileAnalyzedAsync(string filePath, long fileModifiedTicks)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT file_modified_at, analysis_version FROM ai_analysis_status WHERE file_path = @path
            """;
        cmd.Parameters.AddWithValue("@path", filePath);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return false;
        return reader.GetInt64(0) == fileModifiedTicks && reader.GetInt32(1) >= CurrentAnalysisVersion;
    }

    public async Task<IReadOnlyList<(string Path, long ModifiedTicks)>> GetUnanalyzedFilesAsync(
        IReadOnlyList<string> filePaths, IReadOnlyList<long> modifiedTicks)
    {
        var result = new List<(string, long)>();
        // Build lookup of existing analysis status
        var analyzed = new Dictionary<string, (long mtime, int version)>();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT file_path, file_modified_at, analysis_version FROM ai_analysis_status";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                analyzed[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt32(2));
            }
        }

        for (int i = 0; i < filePaths.Count; i++)
        {
            var path = filePaths[i];
            var mtime = modifiedTicks[i];
            if (!analyzed.TryGetValue(path, out var status) ||
                status.mtime != mtime ||
                status.version < CurrentAnalysisVersion)
            {
                result.Add((path, mtime));
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetAnalyzedPathsInDirectoryAsync(string parentPath)
    {
        var paths = new List<string>();
        var prefix = parentPath.EndsWith('/') ? parentPath : parentPath + "/";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT file_path FROM ai_analysis_status
            WHERE file_path LIKE @prefix AND file_path NOT LIKE @subdir
            """;
        cmd.Parameters.AddWithValue("@prefix", prefix + "%");
        cmd.Parameters.AddWithValue("@subdir", prefix + "%/%");

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            paths.Add(reader.GetString(0));

        return paths;
    }

    // ── Save & delete ──

    public async Task SaveAnalysisResultAsync(string filePath, long fileModifiedTicks, ImageAnalysisResult result)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            // Clear old data (supports re-analysis)
            await DeleteAnalysisDataAsync(filePath, transaction);

            var now = DateTime.UtcNow.Ticks;

            // Insert AI tags from Vision classification
            foreach (var label in result.Classifications)
            {
                await InsertTagAsync(filePath, label.TagType, label.DisplayName, label.Confidence, now, transaction);
            }

            // Insert text tags
            foreach (var text in result.RecognizedTexts)
            {
                await InsertTagAsync(filePath, "text", text.Text, text.Confidence, now, transaction);
                foreach (var keyword in text.Keywords)
                {
                    await InsertTagAsync(filePath, "text_summary", keyword, text.Confidence, now, transaction);
                }
            }

            // Insert location tag
            if (result.Location?.PlaceName is { } placeName)
            {
                await InsertTagAsync(filePath, "location", placeName, 1.0f, now, transaction);
            }

            // Insert date tags
            if (result.DateInfo is { } dateInfo)
            {
                await InsertTagAsync(filePath, "date", dateInfo.YearMonth, 1.0f, now, transaction);
                await InsertTagAsync(filePath, "date_day", dateInfo.Day, 1.0f, now, transaction);
            }

            // Insert camera tag
            if (!string.IsNullOrEmpty(result.CameraInfo))
            {
                await InsertTagAsync(filePath, "camera", result.CameraInfo, 1.0f, now, transaction);
            }

            // Insert face observations
            foreach (var face in result.Faces)
            {
                using var faceCmd = _connection.CreateCommand();
                faceCmd.Transaction = transaction;
                faceCmd.CommandText = """
                    INSERT INTO face_observations (file_path, bounding_box_x, bounding_box_y, bounding_box_w, bounding_box_h, feature_print, created_at)
                    VALUES (@path, @x, @y, @w, @h, @fp, @created)
                    """;
                faceCmd.Parameters.AddWithValue("@path", filePath);
                faceCmd.Parameters.AddWithValue("@x", face.BoundingBoxX);
                faceCmd.Parameters.AddWithValue("@y", face.BoundingBoxY);
                faceCmd.Parameters.AddWithValue("@w", face.BoundingBoxW);
                faceCmd.Parameters.AddWithValue("@h", face.BoundingBoxH);
                faceCmd.Parameters.AddWithValue("@fp", (object?)face.FeaturePrint ?? DBNull.Value);
                faceCmd.Parameters.AddWithValue("@created", now);
                await faceCmd.ExecuteNonQueryAsync();
            }

            // Mark analysis complete
            using (var statusCmd = _connection.CreateCommand())
            {
                statusCmd.Transaction = transaction;
                statusCmd.CommandText = """
                    INSERT OR REPLACE INTO ai_analysis_status (file_path, file_modified_at, analyzed_at, analysis_version)
                    VALUES (@path, @mtime, @now, @version)
                    """;
                statusCmd.Parameters.AddWithValue("@path", filePath);
                statusCmd.Parameters.AddWithValue("@mtime", fileModifiedTicks);
                statusCmd.Parameters.AddWithValue("@now", now);
                statusCmd.Parameters.AddWithValue("@version", CurrentAnalysisVersion);
                await statusCmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task DeleteAnalysisForFileAsync(string filePath)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            await DeleteAnalysisDataAsync(filePath, transaction);

            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM ai_analysis_status WHERE file_path = @path";
            cmd.Parameters.AddWithValue("@path", filePath);
            await cmd.ExecuteNonQueryAsync();

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task DeleteAnalysisForFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var path in filePaths)
            {
                await DeleteAnalysisDataAsync(path, transaction);

                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM ai_analysis_status WHERE file_path = @path";
                cmd.Parameters.AddWithValue("@path", path);
                await cmd.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task UpdateFilePathAsync(string oldPath, string newPath)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var table in new[] { "ai_tags", "face_observations", "ai_analysis_status" })
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"UPDATE {table} SET file_path = @new WHERE file_path = @old";
                cmd.Parameters.AddWithValue("@new", newPath);
                cmd.Parameters.AddWithValue("@old", oldPath);
                await cmd.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // ── Tag queries ──

    public async Task<IReadOnlyList<AiTag>> GetTagsForFileAsync(string filePath)
    {
        var tags = new List<AiTag>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_path, tag_type, tag_value, confidence, created_at
            FROM ai_tags WHERE file_path = @path ORDER BY confidence DESC
            """;
        cmd.Parameters.AddWithValue("@path", filePath);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tags.Add(new AiTag
            {
                Id = reader.GetInt32(0),
                FilePath = reader.GetString(1),
                TagType = reader.GetString(2),
                TagValue = reader.GetString(3),
                Confidence = reader.GetFloat(4),
                CreatedAt = new DateTime(reader.GetInt64(5), DateTimeKind.Utc).ToLocalTime()
            });
        }
        return tags;
    }

    public async Task<IReadOnlyList<string>> SearchByTagAsync(string tagValue, string? tagType = null, int limit = 200)
    {
        var paths = new List<string>();
        using var cmd = _connection.CreateCommand();

        // Try FTS5 first
        if (tagType == null)
        {
            cmd.CommandText = """
                SELECT DISTINCT a.file_path FROM ai_tags a
                INNER JOIN ai_tags_fts f ON a.id = f.rowid
                WHERE ai_tags_fts MATCH @query
                LIMIT @limit
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT DISTINCT file_path FROM ai_tags
                WHERE tag_type = @type AND tag_value LIKE @query
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@type", tagType);
        }

        cmd.Parameters.AddWithValue("@query", tagType == null ? $"{tagValue}*" : $"%{tagValue}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                paths.Add(reader.GetString(0));
        }
        catch
        {
            // FTS5 may fail, fall back to LIKE
            if (tagType == null)
            {
                paths.Clear();
                using var fallback = _connection.CreateCommand();
                fallback.CommandText = """
                    SELECT DISTINCT file_path FROM ai_tags
                    WHERE tag_value LIKE @query LIMIT @limit
                    """;
                fallback.Parameters.AddWithValue("@query", $"%{tagValue}%");
                fallback.Parameters.AddWithValue("@limit", limit);
                using var reader2 = await fallback.ExecuteReaderAsync();
                while (await reader2.ReadAsync())
                    paths.Add(reader2.GetString(0));
            }
        }

        return paths;
    }

    public async Task<IReadOnlyList<AiCategory>> SearchCategoriesAsync(string query, int limit = 5)
    {
        var categories = new List<AiCategory>();
        if (string.IsNullOrWhiteSpace(query)) return categories;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT at.tag_type, at.tag_value, COUNT(DISTINCT at.file_path) as file_count,
                   fc.id as cluster_id
            FROM ai_tags at
            LEFT JOIN face_clusters fc ON at.tag_type = 'face' AND fc.display_name = at.tag_value
            WHERE at.tag_value LIKE @query
            GROUP BY at.tag_type, at.tag_value
            ORDER BY file_count DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                categories.Add(new AiCategory
                {
                    TagType = reader.GetString(0),
                    TagValue = reader.GetString(1),
                    FileCount = reader.GetInt32(2),
                    FaceClusterId = reader.IsDBNull(3) ? null : reader.GetInt32(3)
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to search categories with query {Query} in {Method}", query, nameof(SearchCategoriesAsync));
        }

        return categories;
    }

    // ── Face clusters ──

    public async Task<IReadOnlyList<FaceCluster>> GetAllFaceClustersAsync()
    {
        var clusters = new List<FaceCluster>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.display_name, c.representative_face_id, c.created_at, c.updated_at,
                   COUNT(fo.id) as face_count,
                   fo2.file_path as rep_path,
                   fo2.bounding_box_x, fo2.bounding_box_y, fo2.bounding_box_w, fo2.bounding_box_h
            FROM face_clusters c
            LEFT JOIN face_observations fo ON fo.cluster_id = c.id
            LEFT JOIN face_observations fo2 ON fo2.id = c.representative_face_id
            GROUP BY c.id
            ORDER BY face_count DESC
            """;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            clusters.Add(new FaceCluster
            {
                Id = reader.GetInt32(0),
                DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                RepresentativeFaceId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                CreatedAt = new DateTime(reader.GetInt64(3), DateTimeKind.Utc).ToLocalTime(),
                UpdatedAt = new DateTime(reader.GetInt64(4), DateTimeKind.Utc).ToLocalTime(),
                FaceCount = reader.GetInt32(5),
                RepresentativeFacePath = reader.IsDBNull(6) ? null : reader.GetString(6),
                BoundingBoxX = reader.IsDBNull(7) ? 0f : (float)reader.GetDouble(7),
                BoundingBoxY = reader.IsDBNull(8) ? 0f : (float)reader.GetDouble(8),
                BoundingBoxW = reader.IsDBNull(9) ? 0f : (float)reader.GetDouble(9),
                BoundingBoxH = reader.IsDBNull(10) ? 0f : (float)reader.GetDouble(10)
            });
        }
        return clusters;
    }

    public async Task<IReadOnlyList<string>> GetFilePathsForClusterAsync(int clusterId)
    {
        var paths = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT file_path FROM face_observations
            WHERE cluster_id = @id ORDER BY created_at
            """;
        cmd.Parameters.AddWithValue("@id", clusterId);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            paths.Add(reader.GetString(0));

        return paths;
    }

    public async Task SetClusterNameAsync(int clusterId, string name)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            // Update cluster name
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE face_clusters SET display_name = @name, updated_at = @now WHERE id = @id";
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
                cmd.Parameters.AddWithValue("@id", clusterId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Update face tags for all files in this cluster
            var filePaths = new List<string>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT DISTINCT file_path FROM face_observations WHERE cluster_id = @id";
                cmd.Parameters.AddWithValue("@id", clusterId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    filePaths.Add(reader.GetString(0));
            }

            foreach (var path in filePaths)
            {
                // Remove old face tags for this cluster
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM ai_tags WHERE file_path = @path AND tag_type = 'face' AND tag_value = (SELECT display_name FROM face_clusters WHERE id = @id)";
                    cmd.Parameters.AddWithValue("@path", path);
                    cmd.Parameters.AddWithValue("@id", clusterId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Insert new face tag
                await InsertTagAsync(path, "face", name, 1.0f, DateTime.UtcNow.Ticks, transaction);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task MergeClustersAsync(int targetClusterId, int sourceClusterId)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE face_observations SET cluster_id = @target WHERE cluster_id = @source";
                cmd.Parameters.AddWithValue("@target", targetClusterId);
                cmd.Parameters.AddWithValue("@source", sourceClusterId);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM face_clusters WHERE id = @source";
                cmd.Parameters.AddWithValue("@source", sourceClusterId);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE face_clusters SET updated_at = @now WHERE id = @target";
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
                cmd.Parameters.AddWithValue("@target", targetClusterId);
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task RunClusteringAsync(float distanceThreshold = 0.5f)
    {
        // Load unassigned faces
        var unassigned = new List<(int id, string path, byte[] fp)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, file_path, feature_print FROM face_observations WHERE cluster_id IS NULL AND feature_print IS NOT NULL";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var fp = new byte[reader.GetBytes(2, 0, null!, 0, 0)];
                reader.GetBytes(2, 0, fp, 0, fp.Length);
                unassigned.Add((reader.GetInt32(0), reader.GetString(1), fp));
            }
        }

        if (unassigned.Count == 0) return;

        // Load existing cluster representatives
        var clusterReps = new List<(int clusterId, byte[] fp)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.id, fo.feature_print FROM face_clusters c
                INNER JOIN face_observations fo ON fo.id = c.representative_face_id
                WHERE fo.feature_print IS NOT NULL
                """;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var fp = new byte[reader.GetBytes(1, 0, null!, 0, 0)];
                reader.GetBytes(1, 0, fp, 0, fp.Length);
                clusterReps.Add((reader.GetInt32(0), fp));
            }
        }

        var now = DateTime.UtcNow.Ticks;

        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var (faceId, facePath, faceFp) in unassigned)
            {
                int? bestClusterId = null;
                float bestDistance = distanceThreshold;

                foreach (var (clusterId, clusterFp) in clusterReps)
                {
                    var distance = ComputeEuclideanDistance(faceFp, clusterFp);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestClusterId = clusterId;
                    }
                }

                if (bestClusterId.HasValue)
                {
                    // Assign to existing cluster
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = "UPDATE face_observations SET cluster_id = @cid WHERE id = @fid";
                    cmd.Parameters.AddWithValue("@cid", bestClusterId.Value);
                    cmd.Parameters.AddWithValue("@fid", faceId);
                    await cmd.ExecuteNonQueryAsync();
                }
                else
                {
                    // Create new cluster
                    int newClusterId;
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = """
                            INSERT INTO face_clusters (representative_face_id, created_at, updated_at)
                            VALUES (@fid, @now, @now);
                            SELECT last_insert_rowid();
                            """;
                        cmd.Parameters.AddWithValue("@fid", faceId);
                        cmd.Parameters.AddWithValue("@now", now);
                        newClusterId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                    }

                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = "UPDATE face_observations SET cluster_id = @cid WHERE id = @fid";
                        cmd.Parameters.AddWithValue("@cid", newClusterId);
                        cmd.Parameters.AddWithValue("@fid", faceId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    clusterReps.Add((newClusterId, faceFp));
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // ── Categories ──

    public async Task<IReadOnlyList<AiCategory>> GetPopularTextTagsAsync(int limit = 40, int minLength = 2)
    {
        var categories = new List<AiCategory>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT 'text' as tag_type,
                   tag_value,
                   COUNT(DISTINCT file_path) as file_count
            FROM ai_tags
            WHERE tag_type IN ('text', 'text_summary')
              AND LENGTH(tag_value) >= @minLen
            GROUP BY tag_value
            ORDER BY file_count DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@minLen", minLength);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            categories.Add(new AiCategory
            {
                TagType = reader.GetString(0),
                TagValue = reader.GetString(1),
                FileCount = reader.GetInt32(2)
            });
        }
        return categories;
    }

    public async Task<IReadOnlyList<AiCategory>> GetCategoriesByTypeAsync(string tagType)
    {
        var categories = new List<AiCategory>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT tag_type, tag_value, COUNT(DISTINCT file_path) as file_count
            FROM ai_tags WHERE tag_type = @type
            GROUP BY tag_type, tag_value
            ORDER BY file_count DESC
            """;
        cmd.Parameters.AddWithValue("@type", tagType);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            categories.Add(new AiCategory
            {
                TagType = reader.GetString(0),
                TagValue = reader.GetString(1),
                FileCount = reader.GetInt32(2)
            });
        }
        return categories;
    }

    public async Task<IReadOnlyList<string>> GetAllTagTypesAsync()
    {
        var types = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT tag_type FROM ai_tags ORDER BY tag_type";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            types.Add(reader.GetString(0));

        return types;
    }

    public async Task<IReadOnlyList<string>> GetFilePathsForCategoryAsync(string tagType, string tagValue)
    {
        var paths = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT file_path FROM ai_tags
            WHERE tag_type = @type AND tag_value = @value
            ORDER BY file_path
            """;
        cmd.Parameters.AddWithValue("@type", tagType);
        cmd.Parameters.AddWithValue("@value", tagValue);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            paths.Add(reader.GetString(0));

        return paths;
    }

    // ── Private helpers ──

    private async Task DeleteAnalysisDataAsync(string filePath, SqliteTransaction transaction)
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM ai_tags WHERE file_path = @path";
            cmd.Parameters.AddWithValue("@path", filePath);
            await cmd.ExecuteNonQueryAsync();
        }

        // Check if any face_observations being deleted are representative faces
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE face_clusters SET representative_face_id = (
                    SELECT fo.id FROM face_observations fo
                    WHERE fo.cluster_id = face_clusters.id AND fo.file_path != @path
                    LIMIT 1
                )
                WHERE representative_face_id IN (
                    SELECT id FROM face_observations WHERE file_path = @path
                )
                """;
            cmd.Parameters.AddWithValue("@path", filePath);
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM face_observations WHERE file_path = @path";
            cmd.Parameters.AddWithValue("@path", filePath);
            await cmd.ExecuteNonQueryAsync();
        }

        // Clean up empty clusters
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM face_clusters WHERE id NOT IN (
                    SELECT DISTINCT cluster_id FROM face_observations WHERE cluster_id IS NOT NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task InsertTagAsync(string filePath, string tagType, string tagValue, float confidence, long createdAt, SqliteTransaction transaction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO ai_tags (file_path, tag_type, tag_value, confidence, created_at)
            VALUES (@path, @type, @value, @confidence, @created)
            """;
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.Parameters.AddWithValue("@type", tagType);
        cmd.Parameters.AddWithValue("@value", tagValue);
        cmd.Parameters.AddWithValue("@confidence", confidence);
        cmd.Parameters.AddWithValue("@created", createdAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private static float ComputeEuclideanDistance(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return float.MaxValue;

        int floatCount = a.Length / sizeof(float);
        float sum = 0;
        for (int i = 0; i < floatCount; i++)
        {
            float va = BitConverter.ToSingle(a, i * sizeof(float));
            float vb = BitConverter.ToSingle(b, i * sizeof(float));
            float diff = va - vb;
            sum += diff * diff;
        }
        return MathF.Sqrt(sum);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
