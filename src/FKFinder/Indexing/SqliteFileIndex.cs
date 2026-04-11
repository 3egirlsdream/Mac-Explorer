using FKFinder.Models;
using Microsoft.Data.Sqlite;

namespace FKFinder.Indexing;

public class SqliteFileIndex : IFileIndex, IFileIndexWriter, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _databasePath;
    private bool _disposed;

    public SqliteFileIndex(string databasePath)
    {
        _databasePath = databasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // Try to open existing DB; if schema init fails, delete and recreate
        _connection = OpenAndInitialize(databasePath, allowRecreate: true);
    }

    private static SqliteConnection OpenAndInitialize(string dbPath, bool allowRecreate)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        try
        {
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                cmd.ExecuteNonQuery();
            }

            SqliteSchema.Initialize(conn);
            return conn;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SQLite init failed: {ex.Message}");
            conn.Close();
            conn.Dispose();

            if (!allowRecreate)
                throw;

            // Delete corrupted/incompatible DB and recreate from scratch
            try
            {
                foreach (var suffix in new[] { "", "-shm", "-wal", "-journal" })
                {
                    var f = dbPath + suffix;
                    if (File.Exists(f)) File.Delete(f);
                }
                System.Diagnostics.Debug.WriteLine("Deleted old DB, recreating...");
            }
            catch { /* best effort cleanup */ }

            return OpenAndInitialize(dbPath, allowRecreate: false);
        }
    }

    // IFileIndex - Read operations

    public async Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string parentPath)
    {
        var entries = new List<FileSystemEntry>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT path, name, extension, parent_path, size, is_directory,
                   created_at, modified_at, content_type, is_hidden
            FROM files
            WHERE parent_path = @parentPath
            ORDER BY is_directory DESC, name COLLATE NOCASE ASC
            """;
        cmd.Parameters.AddWithValue("@parentPath", parentPath);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    public async Task<FileSystemEntry?> GetEntryAsync(string path)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT path, name, extension, parent_path, size, is_directory,
                   created_at, modified_at, content_type, is_hidden
            FROM files
            WHERE path = @path
            """;
        cmd.Parameters.AddWithValue("@path", path);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadEntry(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<FileSystemEntry>> SearchByNameAsync(string pattern, int limit = 100)
    {
        // If FTS5 is not available, go directly to LIKE search
        if (!SqliteSchema.IsFts5Available)
            return await SearchByNameLikeAsync(pattern, limit);

        var entries = new List<FileSystemEntry>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.path, f.name, f.extension, f.parent_path, f.size, f.is_directory,
                   f.created_at, f.modified_at, f.content_type, f.is_hidden
            FROM files_fts ft
            JOIN files f ON f.rowid = ft.rowid
            WHERE files_fts MATCH @pattern
            ORDER BY rank
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@pattern", $"{pattern}*");
        cmd.Parameters.AddWithValue("@limit", limit);

        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(ReadEntry(reader));
            }
        }
        catch (SqliteException)
        {
            // FTS5 query syntax error, try simple LIKE search as fallback
            return await SearchByNameLikeAsync(pattern, limit);
        }

        return entries;
    }

    public async Task<bool> IsDirectoryFreshAsync(string path, TimeSpan freshnessThreshold)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT last_scanned FROM directories WHERE path = @path
            """;
        cmd.Parameters.AddWithValue("@path", path);

        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value)
            return false;

        var lastScanned = new DateTime((long)result, DateTimeKind.Utc);
        return DateTime.UtcNow - lastScanned < freshnessThreshold;
    }

    // IFileIndexWriter - Write operations

    public async Task UpdateDirectoryAsync(string directoryPath, IReadOnlyList<FileSystemEntry> entries)
    {
        using var transaction = _connection.BeginTransaction();

        try
        {
            // Delete old entries for this directory
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM files WHERE parent_path = @parentPath";
                cmd.Parameters.AddWithValue("@parentPath", directoryPath);
                await cmd.ExecuteNonQueryAsync();
            }

            // Insert new entries
            foreach (var entry in entries)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO files (path, name, extension, parent_path, size, is_directory, created_at, modified_at, content_type, is_hidden, indexed_at)
                    VALUES (@path, @name, @extension, @parentPath, @size, @isDirectory, @createdAt, @modifiedAt, @contentType, @isHidden, @indexedAt)
                    """;
                AddEntryParameters(cmd, entry);
                await cmd.ExecuteNonQueryAsync();
            }

            // Update directory metadata
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO directories (path, file_count, total_size, last_scanned, scan_status)
                    VALUES (@path, @fileCount, @totalSize, @lastScanned, @scanStatus)
                    """;
                cmd.Parameters.AddWithValue("@path", directoryPath);
                cmd.Parameters.AddWithValue("@fileCount", entries.Count);
                cmd.Parameters.AddWithValue("@totalSize", entries.Where(e => !e.IsDirectory).Sum(e => e.Size));
                cmd.Parameters.AddWithValue("@lastScanned", DateTime.UtcNow.Ticks);
                cmd.Parameters.AddWithValue("@scanStatus", "complete");
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

    public async Task RemoveEntryAsync(string path)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddEntryAsync(FileSystemEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO files (path, name, extension, parent_path, size, is_directory, created_at, modified_at, content_type, is_hidden, indexed_at)
            VALUES (@path, @name, @extension, @parentPath, @size, @isDirectory, @createdAt, @modifiedAt, @contentType, @isHidden, @indexedAt)
            """;
        AddEntryParameters(cmd, entry);
        await cmd.ExecuteNonQueryAsync();
    }

    private static FileSystemEntry ReadEntry(SqliteDataReader reader)
    {
        var isDirectory = reader.GetInt32(5) != 0;
        var extension = reader.IsDBNull(2) ? "" : reader.GetString(2);
        return new FileSystemEntry
        {
            FullPath = reader.GetString(0),
            Name = reader.GetString(1),
            Extension = extension,
            Size = reader.GetInt64(4),
            IsDirectory = isDirectory,
            Created = new DateTime(reader.GetInt64(6), DateTimeKind.Utc).ToLocalTime(),
            LastModified = new DateTime(reader.GetInt64(7), DateTimeKind.Utc).ToLocalTime(),
            IsHidden = reader.GetInt32(9) != 0,
            IconKey = isDirectory
                ? ResolveBundleIconKey(extension)
                : ResolveIconKey(extension)
        };
    }

    internal static string ResolveBundleIconKey(string extension) => extension.ToLowerInvariant() switch
    {
        ".app" => "app-bundle",
        ".photoslibrary" or ".photobooth" or ".miximages"
            or ".musiclibrary" or ".tvlibrary" or ".aplibrary"
            or ".fcpbundle" or ".fcpproject"
            or ".garageband" or ".band" or ".logicx"
            or ".bundle" or ".framework" or ".plugin" or ".appex" or ".kext" => "app-bundle",
        ".pvm" or ".vmwarevm" or ".vbox" => "file-vm",
        ".xcodeproj" or ".xcworkspace" or ".playground" => "file-code",
        _ => "folder"
    };

    internal static string ResolveIconKey(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".tif" or ".svg" or ".webp" or ".heic" or ".ico"
                or ".raw" or ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".pef" => "file-image",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" or ".webm" => "file-video",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" => "file-audio",
            ".pdf" => "file-pdf",
            ".doc" or ".docx" or ".odt" or ".pages" => "file-word",
            ".xls" or ".xlsx" or ".csv" or ".numbers" => "file-excel",
            ".ppt" or ".pptx" or ".key" => "file-powerpoint",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".tgz" or ".zst" => "file-archive",
            ".md" or ".mdx" or ".markdown" => "file-markdown",
            ".txt" or ".rtf" or ".log" or ".nfo" or ".ini" or ".cfg" or ".conf" or ".env" => "file-text",
            ".json" or ".yaml" or ".yml" or ".toml" or ".plist" or ".xml" => "file-config",
            ".html" or ".htm" => "file-web",
            ".cs" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".hpp"
                or ".go" or ".rs" or ".swift" or ".kt" or ".rb" or ".php" or ".lua" or ".r" or ".m" or ".mm"
                or ".scala" or ".clj" or ".ex" or ".erl" or ".hs" or ".dart" or ".v" or ".zig"
                or ".css" or ".scss" or ".sass" or ".less" or ".vue" or ".svelte"
                or ".sh" or ".bash" or ".zsh" or ".fish" or ".bat" or ".cmd" or ".ps1"
                or ".sql" or ".graphql" or ".gql" => "file-code",
            ".cer" or ".crt" or ".pem" or ".p12" or ".pfx" or ".der" or ".cert" or ".ca-bundle" => "file-certificate",
            ".pkg" or ".dmg" or ".msi" or ".exe" or ".deb" or ".rpm" or ".appimage" or ".snap" => "file-installer",
            ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot" or ".fon" or ".ttc" => "file-font",
            ".db" or ".sqlite" or ".sqlite3" or ".mdb" or ".accdb" or ".dbf" or ".realm" => "file-database",
            ".epub" or ".mobi" or ".azw" or ".azw3" or ".fb2" or ".djvu" or ".cbz" or ".cbr" => "file-ebook",
            ".psd" or ".psb" or ".ai" or ".eps" or ".sketch" or ".fig" or ".xd" or ".indd" or ".afdesign" or ".afphoto" => "file-design",
            ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" or ".qcow2" => "file-disk-image",
            ".pvm" or ".pvs" or ".hdd" or ".vdi" or ".vmx" or ".vmwarevm" or ".ova" or ".ovf" or ".vbox" => "file-vm",
            ".obj" or ".fbx" or ".stl" or ".blend" or ".3ds" or ".dae" or ".gltf" or ".glb" or ".usdz" or ".step" or ".stp" => "file-3d",
            ".srt" or ".ass" or ".ssa" or ".sub" or ".vtt" or ".idx" or ".lrc" => "file-subtitle",
            _ => "file-generic"
        };
    }

    private static void AddEntryParameters(SqliteCommand cmd, FileSystemEntry entry)
    {
        cmd.Parameters.AddWithValue("@path", entry.FullPath);
        cmd.Parameters.AddWithValue("@name", entry.Name);
        cmd.Parameters.AddWithValue("@extension", string.IsNullOrEmpty(entry.Extension) ? DBNull.Value : entry.Extension);
        cmd.Parameters.AddWithValue("@parentPath", entry.FullPath.StartsWith("/") && entry.FullPath.Count(c => c == '/') == 1 && entry.FullPath == "/"
            ? "/"
            : Path.GetDirectoryName(entry.FullPath) ?? "/");
        cmd.Parameters.AddWithValue("@size", entry.Size);
        cmd.Parameters.AddWithValue("@isDirectory", entry.IsDirectory ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt", entry.Created.ToUniversalTime().Ticks);
        cmd.Parameters.AddWithValue("@modifiedAt", entry.LastModified.ToUniversalTime().Ticks);
        cmd.Parameters.AddWithValue("@contentType", DBNull.Value);
        cmd.Parameters.AddWithValue("@isHidden", entry.IsHidden ? 1 : 0);
        cmd.Parameters.AddWithValue("@indexedAt", DateTime.UtcNow.Ticks);
    }

    private async Task<IReadOnlyList<FileSystemEntry>> SearchByNameLikeAsync(string pattern, int limit)
    {
        var entries = new List<FileSystemEntry>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT path, name, extension, parent_path, size, is_directory,
                   created_at, modified_at, content_type, is_hidden
            FROM files
            WHERE name LIKE @pattern
            ORDER BY name COLLATE NOCASE ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@pattern", $"%{pattern}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
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

    // Icon cache operations

    public string? GetCachedIcon(string appPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT icon_base64 FROM icon_cache WHERE app_path = @path";
        cmd.Parameters.AddWithValue("@path", appPath);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    public void SetCachedIcon(string appPath, string iconBase64)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO icon_cache (app_path, icon_base64, modified_at)
            VALUES (@path, @icon, @modifiedAt)
            """;
        cmd.Parameters.AddWithValue("@path", appPath);
        cmd.Parameters.AddWithValue("@icon", iconBase64);
        cmd.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }
}

public interface IFileIndex
{
    Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string parentPath);
    Task<FileSystemEntry?> GetEntryAsync(string path);
    Task<IReadOnlyList<FileSystemEntry>> SearchByNameAsync(string pattern, int limit = 100);
    Task<bool> IsDirectoryFreshAsync(string path, TimeSpan freshnessThreshold);
}

public interface IFileIndexWriter
{
    Task UpdateDirectoryAsync(string directoryPath, IReadOnlyList<FileSystemEntry> entries);
    Task RemoveEntryAsync(string path);
    Task AddEntryAsync(FileSystemEntry entry);
}
