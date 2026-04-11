using Microsoft.Data.Sqlite;

namespace FKFinder.Services.Impl;

public class SettingsService : ISettingsService
{
    private readonly string _connectionString;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SettingsService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
        LoadAll();
    }

    private void LoadAll()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM app_settings";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _cache[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch { }
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
        catch
        {
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
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO app_settings (key, value) VALUES (@key, @value)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}
