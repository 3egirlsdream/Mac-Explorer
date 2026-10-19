namespace MacExplorer.Models;

public class OpenWithApp
{
    public int Id { get; set; }
    public string BundleId { get; set; } = "";
    public string Label { get; set; } = "";
    public string DisplayLabel => BuiltInOpenWithActions.IsRevealInFinder(BundleId)
        ? Services.Impl.LocalizationText.Get(BuiltInOpenWithActions.RevealInFinderLabel)
        : BuiltInOpenWithActions.IsOpenInTerminal(BundleId)
            ? Services.Impl.LocalizationText.Get(BuiltInOpenWithActions.OpenInTerminalLabel) : Label;
    public bool IsTopLevel { get; set; } = true;
    public int SortOrder { get; set; }
    public string? IconBase64 { get; set; }
}

public class AppListItem
{
    public IReadOnlyList<string> Names { get; set; } = [];
    public string Name { get; set; } = "";
    public string BundleId { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string? IconBase64 { get; set; }
}
