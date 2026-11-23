using System.Collections.Concurrent;
using System.Globalization;
using MacExplorer.Services;

namespace MacExplorer.Tests;

internal sealed class InMemorySettingsService : ISettingsService
{
    private readonly ConcurrentDictionary<string, string> _values;
    private readonly StringComparer _comparer;

    public InMemorySettingsService(bool ignoreCase = true)
    {
        _comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _values = new ConcurrentDictionary<string, string>(_comparer);
    }

    public event Action<string>? SettingChanged;

    public string this[string key]
    {
        get => _values[key];
        set => _values[key] = value;
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public T Get<T>(string key, T defaultValue)
    {
        if (!_values.TryGetValue(key, out var raw)) return defaultValue;
        if (typeof(T) == typeof(string)) return (T)(object)raw;
        if (typeof(T) == typeof(bool) && bool.TryParse(raw, out var b)) return (T)(object)b;
        if (typeof(T) == typeof(int) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return (T)(object)i;
        if (typeof(T) == typeof(long) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return (T)(object)l;
        if (typeof(T) == typeof(double) && double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d)) return (T)(object)d;
        if (typeof(T).IsEnum && Enum.TryParse(typeof(T), raw, true, out var parsed)) return (T)parsed;
        return defaultValue;
    }

    public void Set(string key, string value)
    {
        _values[key] = value;
        SettingChanged?.Invoke(key);
    }

    public void Set<T>(string key, T value) => Set(key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);

    public Dictionary<string, string> GetAll() => new(_values, _comparer);
}
