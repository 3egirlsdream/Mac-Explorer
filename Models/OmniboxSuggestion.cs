using System.Threading.Tasks;

namespace MacExplorer.Models;

public enum OmniboxSuggestionKind
{
    Path,
    Result,
    Command,
    RecentDirectory
}

public sealed record OmniboxSuggestion(
    OmniboxSuggestionKind Kind,
    string Title,
    string Subtitle,
    string Value,
    string IconData,
    string IconColor,
    FileSystemEntry? Entry = null,
    Func<Task>? ExecuteAction = null)
{
    public bool CanLocate => Entry is { IsVirtual: false }
        || Kind is OmniboxSuggestionKind.Path or OmniboxSuggestionKind.RecentDirectory;
}
