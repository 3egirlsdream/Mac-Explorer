namespace MacExplorer.Models;

public class OpenWithApp
{
    public int Id { get; set; }
    public string BundleId { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsTopLevel { get; set; } = true;
    public int SortOrder { get; set; }
    public string? IconBase64 { get; set; }
}

public class AppListItem
{
    public string Name { get; set; } = "";
    public string BundleId { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string? IconBase64 { get; set; }
}
