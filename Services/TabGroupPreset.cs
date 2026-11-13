using System.Text.Json;

namespace MacExplorer.Services;

/// <summary>A reusable single-group snapshot, independent of automatic startup recovery.</summary>
internal sealed record TabGroupPreset(Guid Id, string Name, FolderTabSession Session)
{
    internal static TabGroupPreset? Capture(string name, IEnumerable<string?> paths, FolderTabGroupSession group,
        Guid? id = null, Dictionary<int, DirectoryPreferences>? preferences = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var members = group.TabIndices.ToHashSet();
        var session = FolderTabSession.Capture(paths.Select((path, index) => members.Contains(index) ? path : null),
            group.SelectedIndex, [group], preferences);
        return session.Groups.Length != 1 ? null : new(id ?? Guid.NewGuid(), name.Trim(),
            session with { SelectedIndex = session.Groups[0].SelectedIndex });
    }

    internal FolderTabSession? CreateSession(Func<string, bool> canRestore)
    {
        var session = FolderTabSession.Filter(Session.Paths, Session.SelectedIndex, canRestore, Session.Groups, Session.Preferences);
        if (session.Groups.Length != 1) return null;
        // Each invocation opens an independent, expanded group in the receiving window.
        return session with { SelectedIndex = session.Groups[0].SelectedIndex,
            Groups = [session.Groups[0] with { Id = Guid.NewGuid(), PresetId = Id, Name = Name, IsCollapsed = false }] };
    }
}

internal static class TabGroupPresets
{
    internal const string SettingKey = "tab_group_presets";

    internal static TabGroupPreset[] Read(ISettingsService settings)
    {
        var json = settings.Get(SettingKey);
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            var saved = JsonSerializer.Deserialize<TabGroupPreset[]>(json) ?? [];
            var result = new List<TabGroupPreset>();
            var ids = new HashSet<Guid>();
            foreach (var preset in saved)
            {
                if (preset?.Session?.Paths == null || preset.Session.Groups is not { Length: 1 }
                    || preset.Session.Groups[0]?.TabIndices == null || !ids.Add(preset.Id)) continue;
                if (TabGroupPreset.Capture(preset.Name, preset.Session.Paths, preset.Session.Groups[0], preset.Id, preset.Session.Preferences) is { } normalized)
                    result.Add(normalized);
            }
            return result.ToArray();
        }
        catch (JsonException) { return []; }
    }

    internal static void Save(ISettingsService settings, TabGroupPreset preset)
    {
        var presets = Read(settings).ToList();
        var index = presets.FindIndex(item => item.Id == preset.Id);
        if (index < 0) presets.Add(preset);
        else presets[index] = preset;
        settings.Set(SettingKey, JsonSerializer.Serialize(presets));
    }

    internal static void Delete(ISettingsService settings, Guid id)
        => settings.Set(SettingKey, JsonSerializer.Serialize(Read(settings).Where(item => item.Id != id).ToArray()));
}
