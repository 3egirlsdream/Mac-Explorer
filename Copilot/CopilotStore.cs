using Microsoft.Data.Sqlite;

namespace MacExplorer.Copilot;

public sealed record CopilotTurn(string Role, string Text, DateTimeOffset CreatedAt);
public sealed record CopilotArtifact(string Id, string Kind, string Value, DateTimeOffset CreatedAt);
public sealed record CopilotSessionSummary(string Id, string Title, DateTimeOffset UpdatedAt);

/// <summary>Separate local database so clearing a chat does not change the file index.</summary>
public sealed class CopilotStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public CopilotStore() : this(null) { }

    public CopilotStore(string? databasePath)
    {
        databasePath ??= Path.Combine(RuntimePaths.LocalApplicationData, "MacExplorer", "copilot.db");
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
                id TEXT PRIMARY KEY, maf_state TEXT, updated_at TEXT NOT NULL);
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
    }

    public string CreateSession()
    {
        var id = Guid.NewGuid().ToString("N");
        SaveState(id, null);
        return id;
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
                INSERT INTO sessions(id, maf_state, updated_at) VALUES($id, $state, $time)
                ON CONFLICT(id) DO UPDATE SET maf_state = $state, updated_at = $time
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$state", (object?)mafState ?? DBNull.Value);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
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

    public void DeleteSession(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; DELETE FROM sessions WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }
}
