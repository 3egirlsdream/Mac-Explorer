namespace MacExplorer.Services;

public enum GlobalSearchScope
{
    CurrentFolder,
    UserFolder,
    ThisMac,
    CustomFolders
}

/// <summary>Persists the range selected in the global quick-search panel.</summary>
public interface IGlobalSearchScopeService
{
    GlobalSearchScope Scope { get; set; }

    /// <summary>Folders included when <see cref="Scope"/> is CustomFolders.</summary>
    IReadOnlyList<string> CustomFolders { get; }

    void SetCustomFolders(IEnumerable<string> folders);
}
