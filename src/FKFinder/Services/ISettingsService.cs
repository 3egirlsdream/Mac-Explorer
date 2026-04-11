namespace FKFinder.Services;

public interface ISettingsService
{
    string? Get(string key);
    T Get<T>(string key, T defaultValue);
    void Set(string key, string value);
    void Set<T>(string key, T value);
    Dictionary<string, string> GetAll();
}
