namespace MacExplorer.Services.Impl;

public sealed class GlobalSearchScopeService : IGlobalSearchScopeService
{
    private const string ScopeSettingKey = "global_search_scope";
    private const string CustomFoldersSettingKey = "global_search_custom_folders";
    private readonly ISettingsService _settings;
    private GlobalSearchScope _scope;
    private readonly List<string> _customFolders;

    /// <summary>
    /// There are no implicit search locations. Users choose every custom folder
    /// explicitly from Settings → Locations or the global-search add button.
    /// </summary>
    public static IReadOnlyList<string> DefaultSearchFolders => [];

    // Paths that earlier builds inserted automatically. Remove them once from
    // persisted settings so an upgrade does not silently keep scanning them.
    private static IReadOnlyList<string> LegacyDefaultSearchFolders =>
    [
        "/Applications",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
        "/System/Applications"
    ];

    public GlobalSearchScopeService(ISettingsService settings)
    {
        _settings = settings;
        _scope = settings.Get(ScopeSettingKey, GlobalSearchScope.ThisMac);
        if (_scope == GlobalSearchScope.UserFolder)
        {
            // The user-folder scope was removed from the UI. Migrate an older
            // saved selection to the broad, still-visible Mac scope.
            _scope = GlobalSearchScope.ThisMac;
            settings.Set(ScopeSettingKey, _scope);
        }
        var storedFolders = settings.Get(CustomFoldersSettingKey);
        _customFolders = storedFolders == null
            ? []
            : ParseFolders(storedFolders);

        // Remove paths that an intermediate build added automatically. Keep
        // explicitly selected folders intact, and persist the cleanup once.
        var removedLegacyDefaults = _customFolders.RemoveAll(IsLegacyDefaultPath);
        if (removedLegacyDefaults > 0)
        {
            PersistFolders();
        }
    }

    public GlobalSearchScope Scope
    {
        get => _scope;
        set
        {
            _scope = value;
            _settings.Set(ScopeSettingKey, value);
        }
    }

    public IReadOnlyList<string> CustomFolders => _customFolders;

    public void SetCustomFolders(IEnumerable<string> folders)
    {
        // Materialize before clearing the backing list. Callers commonly pass
        // CustomFolders.Concat(...), which otherwise would enumerate only the
        // newly added item after Clear() and silently replace the whole list.
        var requestedFolders = folders?.ToArray() ?? [];
        _customFolders.Clear();
        foreach (var folder in requestedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string fullPath;
            try
            {
                fullPath = NormalizePath(folder);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!_customFolders.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                _customFolders.Add(fullPath);
        }

        PersistFolders();
    }

    private void PersistFolders() =>
        _settings.Set(CustomFoldersSettingKey, string.Join("\n", _customFolders));

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
    }

    private static List<string> ParseFolders(string value)
    {
        var folders = new List<string>();
        foreach (var folder in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var fullPath = NormalizePath(folder);
                if (!folders.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    folders.Add(fullPath);
            }
            catch (ArgumentException) { }
        }

        return folders;
    }

    private static bool IsLegacyDefaultPath(string path) =>
        LegacyDefaultSearchFolders
            .Select(NormalizePath)
            .Contains(path, StringComparer.OrdinalIgnoreCase);
}
