namespace MacExplorer.Models;

public enum FileTagKind
{
    FinderColor,
    Custom
}

public sealed record FileTag(
    string Name,
    string ColorHex,
    FileTagKind Kind,
    int ItemCount = 0,
    int ColorId = 0,
    bool IsPinned = false,
    int SortOrder = 0)
{
    public bool IsFinderColor => Kind == FileTagKind.FinderColor;
    public bool IsCustom => Kind == FileTagKind.Custom;
    public string VirtualPath => Services.TagPathHelper.Build(this);
}

public static class FileTagCatalog
{
    public const string CustomTagColor = "#8E8E93";

    public static IReadOnlyList<FileTag> FinderColors { get; } =
    [
        new("红色", "#FF3B30", FileTagKind.FinderColor, ColorId: 6),
        new("橙色", "#FF9500", FileTagKind.FinderColor, ColorId: 7),
        new("黄色", "#FFCC00", FileTagKind.FinderColor, ColorId: 5),
        new("绿色", "#34C759", FileTagKind.FinderColor, ColorId: 2),
        new("蓝色", "#007AFF", FileTagKind.FinderColor, ColorId: 4),
        new("紫色", "#AF52DE", FileTagKind.FinderColor, ColorId: 3),
        new("灰色", "#8E8E93", FileTagKind.FinderColor, ColorId: 1)
    ];

    private static readonly Dictionary<string, string> CanonicalColorNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["红色"] = "红色", ["Red"] = "红色",
            ["橙色"] = "橙色", ["Orange"] = "橙色",
            ["黄色"] = "黄色", ["Yellow"] = "黄色",
            ["绿色"] = "绿色", ["Green"] = "绿色",
            ["蓝色"] = "蓝色", ["Blue"] = "蓝色",
            ["紫色"] = "紫色", ["Purple"] = "紫色",
            ["灰色"] = "灰色", ["Gray"] = "灰色", ["Grey"] = "灰色"
        };

    private static readonly Dictionary<string, string[]> ColorAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["红色"] = ["红色", "Red"],
            ["橙色"] = ["橙色", "Orange"],
            ["黄色"] = ["黄色", "Yellow"],
            ["绿色"] = ["绿色", "Green"],
            ["蓝色"] = ["蓝色", "Blue"],
            ["紫色"] = ["紫色", "Purple"],
            ["灰色"] = ["灰色", "Gray", "Grey"]
        };

    public static string ColorHex(int colorId) => FinderColors.FirstOrDefault(t => t.ColorId == colorId)?.ColorHex ?? CustomTagColor;

    public static bool TryGetFinderColor(string? name, out FileTag tag)
    {
        var canonicalName = NormalizeName(name);
        tag = FinderColors.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, canonicalName, StringComparison.OrdinalIgnoreCase))!;
        return tag != null;
    }

    public static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return CanonicalColorNames.TryGetValue(trimmed, out var canonical)
            ? canonical
            : trimmed;
    }

    public static IReadOnlyList<string> GetFinderAliases(string name)
    {
        var canonicalName = NormalizeName(name);
        return ColorAliases.TryGetValue(canonicalName, out var aliases)
            ? aliases
            : [canonicalName];
    }
}
