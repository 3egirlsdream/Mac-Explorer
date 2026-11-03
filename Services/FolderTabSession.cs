using System.Text.Json;

namespace MacExplorer.Services;

/// <summary>A folder-only session. Virtual pages, searches and credentials are not persisted.</summary>
internal sealed record FolderTabSession(string[] Paths, int SelectedIndex)
{
    internal const string EnabledKey = "restore_folder_tabs";
    internal const string SessionKey = "navigation_folder_tabs";

    internal static FolderTabSession Capture(IEnumerable<string?> paths, int selectedIndex)
        => Filter(paths, selectedIndex, _ => true);

    internal static FolderTabSession? Read(ISettingsService settings, Func<string, bool> canRestore)
    {
        if (!settings.Get(EnabledKey, false)) return null;
        var json = settings.Get(SessionKey);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<FolderTabSession>(json);
            if (saved?.Paths == null) return null;
            var session = Filter(saved.Paths, saved.SelectedIndex, canRestore);
            return session.Paths.Length == 0 ? null : session;
        }
        catch (JsonException) { return null; }
    }

    internal void Save(ISettingsService settings)
    {
        if (settings.Get(EnabledKey, false))
            settings.Set(SessionKey, JsonSerializer.Serialize(this));
    }

    private static FolderTabSession Filter(IEnumerable<string?> paths, int selectedIndex, Func<string, bool> accept)
    {
        var result = new List<string>();
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
                        result.Add(path);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            index++;
        }
        return new FolderTabSession(result.ToArray(), selected);
    }
}
