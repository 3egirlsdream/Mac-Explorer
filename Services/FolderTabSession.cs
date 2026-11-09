using System.Text.Json;
using MacExplorer.ViewModels;

namespace MacExplorer.Services;

internal sealed record FolderTabGroupSession(Guid Id, string Name, int ColorIndex, bool IsCollapsed,
    int[] TabIndices, PaneLayout Layout, int[] PaneIndices, int SelectedIndex, int ActivePaneSlotIndex)
{
    public Guid? PresetId { get; init; }
}

/// <summary>A folder-only session. Virtual pages, searches and credentials are not persisted.</summary>
internal sealed record FolderTabSession(string[] Paths, int SelectedIndex)
{
    public FolderTabGroupSession[] Groups { get; init; } = [];

    internal const string EnabledKey = "restore_folder_tabs";
    internal const string SessionKey = "navigation_folder_tabs";

    internal static FolderTabSession Capture(IEnumerable<string?> paths, int selectedIndex, FolderTabGroupSession[]? groups = null)
        => Filter(paths, selectedIndex, _ => true, groups);

    internal static FolderTabSession? Read(ISettingsService settings, Func<string, bool> canRestore)
    {
        if (!settings.Get(EnabledKey, false)) return null;
        var json = settings.Get(SessionKey);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<FolderTabSession>(json);
            if (saved?.Paths == null) return null;
            var session = Filter(saved.Paths, saved.SelectedIndex, canRestore, saved.Groups);
            return session.Paths.Length == 0 ? null : session;
        }
        catch (JsonException) { return null; }
    }

    internal void Save(ISettingsService settings)
    {
        if (settings.Get(EnabledKey, false))
            settings.Set(SessionKey, JsonSerializer.Serialize(this));
    }

    internal static FolderTabSession Filter(IEnumerable<string?> paths, int selectedIndex, Func<string, bool> accept, FolderTabGroupSession[]? groups = null)
    {
        var result = new List<string>();
        var remap = new Dictionary<int, int>();
        var selected = 0;
        var index = 0;
        foreach (var path in paths)
        {
            // Preserve order and duplicate folder tabs. Never turn a relative/virtual path into a local path.
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.Contains('\0'))
            {
                try
                {
                    if (accept(path))
                    {
                        if (index == selectedIndex) selected = result.Count;
                        remap[index] = result.Count;
                        result.Add(path);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            index++;
        }
        var claimed = new HashSet<int>();
        var ids = new HashSet<Guid>();
        var filteredGroups = new List<FolderTabGroupSession>();
        foreach (var group in groups ?? [])
        {
            if (group == null || group.TabIndices == null || !ids.Add(group.Id)) continue;
            var members = group.TabIndices.Where(remap.ContainsKey).Select(i => remap[i])
                .Distinct().Where(claimed.Add).Order().ToArray();
            if (members.Length == 0) continue;
            var layout = Enum.IsDefined(group.Layout) ? group.Layout : PaneLayout.Single;
            layout = MainWindowViewModel.FitLayout(layout, members.Length);
            var panes = (group.PaneIndices ?? []).Where(remap.ContainsKey).Select(i => remap[i])
                .Where(members.Contains).Distinct().ToList();
            foreach (var member in members)
                if (!panes.Contains(member)) panes.Add(member);
            panes = panes.Take(MainWindowViewModel.GetPaneCount(layout)).ToList();
            var activeSlot = Math.Clamp(group.ActivePaneSlotIndex, 0, panes.Count - 1);
            var groupSelected = remap.TryGetValue(group.SelectedIndex, out var mapped) && members.Contains(mapped)
                ? mapped : panes[activeSlot];
            if (!panes.Contains(groupSelected)) panes[activeSlot] = groupSelected;
            filteredGroups.Add(group with
            {
                Name = string.IsNullOrWhiteSpace(group.Name) ? "" : group.Name.Trim(),
                ColorIndex = Math.Clamp(group.ColorIndex, 0, TabGroupViewModel.ColorCount - 1),
                TabIndices = members, Layout = layout, PaneIndices = panes.ToArray(),
                SelectedIndex = groupSelected, ActivePaneSlotIndex = panes.IndexOf(groupSelected)
            });
        }
        return new FolderTabSession(result.ToArray(), selected) { Groups = filteredGroups.ToArray() };
    }
}

/// <summary>Window boundaries and active tabs are retained independently.</summary>
internal sealed record FolderWindowSession(FolderTabSession[] Windows, int SelectedWindowIndex)
{
    internal static FolderWindowSession Capture(IEnumerable<FolderTabSession> windows, int selectedWindowIndex)
        => Filter(windows, selectedWindowIndex, _ => true);

    internal static FolderWindowSession? Read(ISettingsService settings, Func<string, bool> canRestore)
    {
        if (!settings.Get(FolderTabSession.EnabledKey, false)) return null;
        var json = settings.Get(FolderTabSession.SessionKey);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            FolderWindowSession? saved;
            if (document.RootElement.TryGetProperty(nameof(Windows), out _))
                saved = JsonSerializer.Deserialize<FolderWindowSession>(json);
            else
            {
                // The first implementation stored a single tab strip.
                var tabs = JsonSerializer.Deserialize<FolderTabSession>(json);
                saved = tabs?.Paths == null ? null : new FolderWindowSession([tabs], 0);
            }
            if (saved?.Windows == null) return null;
            var session = Filter(saved.Windows, saved.SelectedWindowIndex, canRestore);
            return session.Windows.Length == 0 ? null : session;
        }
        catch (JsonException) { return null; }
    }

    internal void Save(ISettingsService settings)
    {
        if (settings.Get(FolderTabSession.EnabledKey, false))
            settings.Set(FolderTabSession.SessionKey, JsonSerializer.Serialize(this));
    }

    private static FolderWindowSession Filter(IEnumerable<FolderTabSession?> windows,
        int selectedWindowIndex, Func<string, bool> canRestore)
    {
        var result = new List<FolderTabSession>();
        var selected = 0;
        var index = 0;
        foreach (var window in windows)
        {
            if (window?.Paths != null)
            {
                var tabs = FolderTabSession.Filter(window.Paths, window.SelectedIndex, canRestore, window.Groups);
                if (tabs.Paths.Length > 0)
                {
                    if (index == selectedWindowIndex) selected = result.Count;
                    result.Add(tabs);
                }
            }
            index++;
        }
        return new FolderWindowSession(result.ToArray(), selected);
    }
}
