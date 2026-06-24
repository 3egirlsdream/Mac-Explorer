namespace MacExplorer.Models;

public enum OmniboxSuggestionKind
{
    Path,
    Result
}

public sealed record OmniboxSuggestion(
    OmniboxSuggestionKind Kind,
    string Title,
    string Subtitle,
    string Value,
    string IconData,
    string IconColor,
    FileSystemEntry? Entry = null);
