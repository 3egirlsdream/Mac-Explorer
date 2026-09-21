using System.Text.Json;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

/// <summary>References to real folders and existing tags; never owns or alters their files.</summary>
public sealed class FileDeliveryService : IDisposable
{
    internal const string PreferencesKey = "file_delivery_preferences_v1";
    internal const string EnabledKey = "file_delivery_enabled";
    private readonly ISettingsService _settings;
    private readonly IFileTagService _tags;
    public FileDeliveryPreferences Preferences { get; private set; }
    public event EventHandler? EntriesChanged;
    public event EventHandler? EnabledChanged;

    public FileDeliveryService(ISettingsService settings, IFileTagService tags)
    {
        _settings = settings;
        _tags = tags;
        var raw = settings.Get(PreferencesKey);
        try { Preferences = raw == null ? CreateDefaults() : JsonSerializer.Deserialize<FileDeliveryPreferences>(raw) ?? new(); }
        catch (JsonException) { Preferences = new(); }
        // Only an absent key is first use. An intentionally empty configuration stays empty.
        if (raw == null) Save();
        _tags.TagRenamed += OnTagRenamed;
    }

    public bool Enabled
    {
        get => _settings.Get(EnabledKey, true);
        set { _settings.Set(EnabledKey, value); EnabledChanged?.Invoke(this, EventArgs.Empty); }
    }

    private static FileDeliveryPreferences CreateDefaults()
    {
        var home = RuntimePaths.HomeDirectory;
        var downloads = new FileDeliveryEntry(Guid.NewGuid().ToString("N"), "下载", Path.Combine(home, "Downloads"));
        return new() { Entries = [downloads, new(Guid.NewGuid().ToString("N"), "桌面", Path.Combine(home, "Desktop"))], SelectedId = downloads.Id };
    }

    public void AddFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new ArgumentException("请选择可访问的本地文件夹。");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Add(new(Guid.NewGuid().ToString("N"), Path.GetFileName(path) is { Length: > 0 } name ? name : path, path));
    }

    public void AddTag(FileTag tag) => Add(new(Guid.NewGuid().ToString("N"), tag.Name, tag.VirtualPath));

    private void Add(FileDeliveryEntry entry)
    {
        if (Preferences.Entries.Any(e => e.Location == entry.Location)) return;
        Preferences.Entries.Add(entry);
        CommitEntries();
    }

    public void Remove(string id)
    {
        Preferences.Entries.RemoveAll(e => e.Id == id);
        if (Preferences.SelectedId == id) Preferences = Preferences with { SelectedId = Preferences.Entries.FirstOrDefault()?.Id };
        CommitEntries();
    }

    public void Move(string id, int delta)
    {
        var index = Preferences.Entries.FindIndex(e => e.Id == id);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= Preferences.Entries.Count) return;
        (Preferences.Entries[index], Preferences.Entries[next]) = (Preferences.Entries[next], Preferences.Entries[index]);
        CommitEntries();
    }

    public void SavePosition(string id, string location, FileListScrollAnchor? anchor, double offset)
    {
        var index = Preferences.Entries.FindIndex(e => e.Id == id);
        if (index < 0) return;
        Preferences.Entries[index] = Preferences.Entries[index] with
        { CurrentLocation = location, ScrollAnchor = anchor, ScrollOffset = offset };
        Preferences = Preferences with { SelectedId = id };
        Save();
    }

    internal static string ResolveLocation(FileDeliveryEntry entry)
        => entry.CurrentLocation is { } location && (TagPathHelper.IsTagPath(location) || Directory.Exists(location))
            ? location : entry.Location;

    private void OnTagRenamed(object? sender, TagRenamedEventArgs e)
    {
        var affected = Preferences.Entries.Where(entry => TagPathHelper.TryParse(entry.Location, out var tag)
            && string.Equals(tag.Name, e.OldName, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var entry in affected)
        {
            if (e.NewName == null) { Preferences.Entries.Remove(entry); continue; }
            TagPathHelper.TryParse(entry.Location, out var tag);
            var location = TagPathHelper.Build(e.NewName, tag.Kind);
            var index = Preferences.Entries.IndexOf(entry);
            Preferences.Entries[index] = entry with { Name = e.NewName, Location = location,
                CurrentLocation = entry.CurrentLocation == entry.Location ? location : entry.CurrentLocation };
        }
        if (affected.Length > 0) CommitEntries();
    }

    private void CommitEntries()
    {
        if (!Preferences.Entries.Any(e => e.Id == Preferences.SelectedId))
            Preferences = Preferences with { SelectedId = Preferences.Entries.FirstOrDefault()?.Id };
        Save();
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Save() => _settings.Set(PreferencesKey, JsonSerializer.Serialize(Preferences));
    public void Dispose() => _tags.TagRenamed -= OnTagRenamed;
}

/// <summary>Panel presentation preferences must not overwrite the main window's preferences.</summary>
internal sealed class FileDeliverySettings(ISettingsService inner) : ISettingsService
{
    private const string Prefix = "file_delivery_view_";
    public string? Get(string key) => inner.Get(Prefix + key);
    public T Get<T>(string key, T defaultValue) => inner.Get(Prefix + key, defaultValue);
    public void Set(string key, string value) => inner.Set(Prefix + key, value);
    public void Set<T>(string key, T value) => inner.Set(Prefix + key, value);
    public Dictionary<string, string> GetAll() => inner.GetAll().Where(p => p.Key.StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(p => p.Key[Prefix.Length..], p => p.Value);
}
