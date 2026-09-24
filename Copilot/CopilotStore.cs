using Microsoft.Data.Sqlite;

namespace MacExplorer.Copilot;

public sealed record CopilotTurn(string Role, string Text, DateTimeOffset CreatedAt);
public sealed record CopilotArtifact(string Id, string Kind, string Value, DateTimeOffset CreatedAt);
public sealed record CopilotSessionSummary(string Id, string Title, DateTimeOffset UpdatedAt);

/// <summary>Separate local database so clearing a chat does not change the file index.</summary>
public sealed class CopilotStore
{
    private static readonly object LeaseGate = new();
    private static readonly Dictionary<string, string> Leases = new(StringComparer.Ordinal);
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly object _gate = new();

    public CopilotStore() : this(null) { }

    public CopilotStore(string? databasePath)
    {
        databasePath ??= Path.Combine(RuntimePaths.LocalApplicationData, "MacExplorer", "copilot.db");
        _databasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY, maf_state TEXT, updated_at TEXT NOT NULL,
                recipient_endpoint TEXT, recipient_model TEXT, pending_approval INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS turns (
                session_id TEXT NOT NULL, ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
                role TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS artifacts (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, kind TEXT NOT NULL,
                value TEXT NOT NULL, created_at TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS reasoning_replay (
                ordinal INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
                response_id TEXT NOT NULL, content TEXT NOT NULL, tool_call_ids TEXT NOT NULL,
                reasoning_content TEXT NOT NULL,
                UNIQUE(session_id, response_id),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            """;
        command.ExecuteNonQuery();
        using var columns = db.CreateCommand();
        columns.CommandText = "PRAGMA table_info(sessions)";
        using var reader = columns.ExecuteReader();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) existing.Add(reader.GetString(1));
        reader.Close();
        foreach (var (name, type) in new[] { ("recipient_endpoint", "TEXT"), ("recipient_model", "TEXT"),
                     ("pending_approval", "INTEGER NOT NULL DEFAULT 0") })
        {
            if (existing.Contains(name)) continue;
            using var migration = db.CreateCommand();
            migration.CommandText = $"ALTER TABLE sessions ADD COLUMN {name} {type}";
            migration.ExecuteNonQuery();
        }
    }

    public string CreateSession()
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO sessions(id, updated_at) VALUES($id, $time)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        return id;
    }

    public bool TryAcquireSession(string id, string owner)
    {
        lock (LeaseGate)
        {
            var key = _databasePath + "\n" + id;
            if (Leases.TryGetValue(key, out var holder) && holder != owner) return false;
            if (!SessionExists(id)) return false;
            Leases[key] = owner;
            return true;
        }
    }

    public void ReleaseSession(string id, string owner)
    {
        lock (LeaseGate)
        {
            var key = _databasePath + "\n" + id;
            if (Leases.TryGetValue(key, out var holder) && holder == owner) Leases.Remove(key);
        }
    }

    public (string? Endpoint, string? Model, bool Pending, bool HasHistory) GetSessionBoundary(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT recipient_endpoint, recipient_model, pending_approval, maf_state IS NOT NULL OR EXISTS(SELECT 1 FROM turns WHERE session_id=$id) FROM sessions WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("会话已删除。");
            return (reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2) != 0,
                reader.GetInt64(3) != 0);
        }
    }

    public void SetSessionBoundary(string id, string endpoint, string model, bool pending = false)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE sessions SET recipient_endpoint=$endpoint, recipient_model=$model, pending_approval=$pending WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$endpoint", endpoint);
            command.Parameters.AddWithValue("$model", model);
            command.Parameters.AddWithValue("$pending", pending ? 1 : 0);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("会话已删除。");
        }
    }

    public void SetPendingApproval(string id, bool pending)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE sessions SET pending_approval=$pending WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$pending", pending ? 1 : 0);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("会话已删除。");
        }
    }

    private bool SessionExists(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT 1 FROM sessions WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() != null;
        }
    }

    public string? LatestSessionId()
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT s.id FROM sessions s
                ORDER BY COALESCE((SELECT MAX(t.created_at) FROM turns t WHERE t.session_id=s.id), s.updated_at) DESC,
                         s.updated_at DESC LIMIT 1
                """;
            return command.ExecuteScalar() as string;
        }
    }

    public IReadOnlyList<CopilotSessionSummary> ListSessions()
    {
        var sessions = new List<CopilotSessionSummary>();
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT s.id,
                       COALESCE((SELECT t.text FROM turns t
                                 WHERE t.session_id=s.id AND t.role='user'
                                 ORDER BY t.ordinal LIMIT 1), ''),
                       COALESCE((SELECT MAX(t.created_at) FROM turns t WHERE t.session_id=s.id), s.updated_at)
                FROM sessions s
                ORDER BY 3 DESC, s.updated_at DESC
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var title = string.Join(' ', reader.GetString(1).Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                sessions.Add(new(reader.GetString(0), string.IsNullOrEmpty(title) ? "新对话" : title,
                    DateTimeOffset.Parse(reader.GetString(2))));
            }
        }
        return sessions;
    }

    public void SaveState(string id, string? mafState)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                UPDATE sessions SET maf_state = $state, updated_at = $time WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$state", (object?)mafState ?? DBNull.Value);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("会话已删除。");
        }
    }

    public string? LoadState(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT maf_state FROM sessions WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as string;
        }
    }

    public void AddTurn(string id, string role, string text)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO turns(session_id, role, text, created_at) VALUES($id,$role,$text,$time)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<CopilotTurn> GetTurns(string id)
    {
        var turns = new List<CopilotTurn>();
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT role, text, created_at FROM turns WHERE session_id=$id ORDER BY ordinal";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) turns.Add(new(reader.GetString(0), reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        }
        return turns;
    }

    public void SaveArtifact(string id, string sessionId, string kind, string value)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO artifacts(id,session_id,kind,value,created_at) VALUES($id,$session,$kind,$value,$time)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public CopilotArtifact? GetArtifact(string sessionId, string artifactId)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT id,kind,value,created_at FROM artifacts WHERE session_id=$session AND id=$id";
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$id", artifactId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3))) : null;
        }
    }

    public IReadOnlyList<(string Id, string Kind)> ListArtifacts(string sessionId, int limit = 20)
    {
        var result = new List<(string, string)>();
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT id,kind FROM artifacts WHERE session_id=$session ORDER BY created_at DESC LIMIT $limit";
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    public void SaveReasoning(string sessionId, string responseId, string content,
        string toolCallIdsJson, string reasoningContent)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO reasoning_replay
                    (session_id,response_id,content,tool_call_ids,reasoning_content)
                VALUES($session,$response,$content,$calls,$reasoning)
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$response", responseId);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$calls", toolCallIdsJson);
            command.Parameters.AddWithValue("$reasoning", reasoningContent);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<(string Content, string ToolCallIdsJson, string ReasoningContent)> LoadReasoning(string sessionId)
    {
        var result = new List<(string, string, string)>();
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT content,tool_call_ids,reasoning_content FROM reasoning_replay
                WHERE session_id=$session ORDER BY ordinal
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    public void DeleteSession(string id, string? owner = null)
    {
        lock (LeaseGate)
        {
            var key = _databasePath + "\n" + id;
            if (Leases.TryGetValue(key, out var holder) && holder != owner)
                throw new InvalidOperationException("该会话正在另一窗口使用。");
            lock (_gate)
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = "DELETE FROM sessions WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            Leases.Remove(key);
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON";
        pragma.ExecuteNonQuery();
        return db;
    }
}
