using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class SettingsService : ISettingsService, IDisposable
{
    private bool _disposed;
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
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
        return _cache.TryGetValue(key, out var value) ? value : null;
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (!_cache.TryGetValue(key, out var raw))
            return defaultValue;

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
        _cache[key] = value;
        Persist(key, value);
    }

    public void Set<T>(string key, T value)
    {
        var str = value?.ToString() ?? "";
        _cache[key] = str;
        Persist(key, str);
    }

    public Dictionary<string, string> GetAll()
    {
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
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
