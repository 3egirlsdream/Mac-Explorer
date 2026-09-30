using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class SettingsService : ISettingsService, IDisposable
{
    public event Action<string>? SettingChanged;
    private bool _disposed;
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly object _persistLock = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private Task _pendingWrite = Task.CompletedTask;
    private readonly ILogger<SettingsService>? _logger;

    public SettingsService(DatabaseConnectionFactory connectionFactory, ILogger<SettingsService>? logger = null)
    {
        _connection = connectionFactory.GetConnection();
        _logger = logger;
        LoadAll();
    }

    private void LoadAll()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM app_settings";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _cache[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load settings in {Method}", nameof(LoadAll));
        }
    }

    public string? Get(string key)
    {
        lock (_lock)
            return _cache.TryGetValue(key, out var value) ? value : null;
    }

    public T Get<T>(string key, T defaultValue)
    {
        string? raw;
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out raw))
                return defaultValue;
        }

        try
        {
            var targetType = typeof(T);

            if (targetType.IsEnum)
                return (T)Enum.Parse(targetType, raw, ignoreCase: true);

            if (targetType == typeof(bool))
                return (T)(object)bool.Parse(raw);

            if (targetType == typeof(int))
                return (T)(object)int.Parse(raw);

            if (targetType == typeof(double))
                return (T)(object)double.Parse(raw);

            return (T)(object)raw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse setting {Key} to type {Type}, returning default", key, typeof(T).Name);
            return defaultValue;
        }
    }

    public void Set(string key, string value)
    {
        bool changed;
        lock (_persistLock)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                changed = !_cache.TryGetValue(key, out var previous) || previous != value;
                _cache[key] = value;
                _pending.Remove(key);
            }
            Persist(key, value);
        }
        if (changed) SettingChanged?.Invoke(key);
    }

    // Noncritical navigation state is available immediately; SQLite writer waits stay off the UI thread.
    public void SetDeferred(string key, string value)
    {
        bool changed;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = !_cache.TryGetValue(key, out var previous) || previous != value;
            _cache[key] = value;
            _pending.Add(key);
            if (_pendingWrite.IsCompleted) _pendingWrite = Task.Run(DrainPending);
        }
        if (changed) SettingChanged?.Invoke(key);
    }

    private void DrainPending()
    {
        while (true)
        {
            lock (_persistLock)
            {
                KeyValuePair<string, string>[] values;
                lock (_lock)
                {
                    if (_pending.Count == 0)
                    {
                        // Mark idle while holding the cache lock so a new write always starts a worker.
                        _pendingWrite = Task.CompletedTask;
                        return;
                    }
                    values = _pending.Select(key => new KeyValuePair<string, string>(key, _cache[key])).ToArray();
                    _pending.Clear();
                }
                foreach (var (key, value) in values) Persist(key, value);
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        Set(key, value?.ToString() ?? "");
    }

    public Dictionary<string, string> GetAll()
    {
        lock (_lock)
            return new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase);
    }

    private void Persist(string key, string value)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO app_settings (key, value) VALUES (@key, @value)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist setting {Key} in {Method}", key, nameof(Persist));
        }
    }

    public void Dispose()
    {
        Task pending;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pendingWrite;
        }
        pending.GetAwaiter().GetResult();
        lock (_persistLock)
        {
            _connection.Close();
            _connection.Dispose();
        }
    }
}
