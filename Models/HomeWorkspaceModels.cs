namespace MacExplorer.Models;

/// <summary>Preferred grid dimensions, not pixels. Narrow windows never overwrite this preference.</summary>
public sealed record HomeFolderLayout(int Columns = 4, int Rows = 2)
{
    public const double CellWidth = 80;
    public const double CellHeight = 82;
    public const double HorizontalChrome = 28;
    public const double VerticalChrome = 58;
    public HomeFolderLayout Normalize() => new(Math.Clamp(Columns, 2, 8), Math.Clamp(Rows, 2, 5));
    public int Capacity => Math.Clamp(Columns, 1, 8) * Math.Clamp(Rows, 2, 5);
    public double Width => Math.Clamp(Columns, 1, 8) * CellWidth + HorizontalChrome;
    public double Height => Math.Clamp(Rows, 2, 5) * CellHeight + VerticalChrome;

    public HomeFolderLayout Fit(double availableWidth)
    {
        var normalized = Normalize();
        var columns = Math.Max(1, (int)Math.Floor(Math.Max(0, availableWidth - HorizontalChrome) / CellWidth));
        // The viewport may show one column, even though the user's saved preference starts at two.
        return new(Math.Min(normalized.Columns, columns), normalized.Rows);
    }

    public static HomeFolderLayout FromSize(double width, double height)
        => new HomeFolderLayout((int)Math.Round((width - HorizontalChrome) / CellWidth),
            (int)Math.Round((height - VerticalChrome) / CellHeight)).Normalize();
}

public sealed record HomeUsageEntry(string Path, bool IsDirectory, int UseCount, DateTime LastUsedUtc)
{
    public DateTime AddedUtc { get; init; }

    // A file that was popular months ago should not permanently displace current work.
    public double Score(DateTime nowUtc)
        => Math.Log2(UseCount + 1d) * Math.Exp(-Math.Max(0, (nowUtc - LastUsedUtc).TotalDays) / 30d);
}

public enum HomeScriptMenuIcon
{
    None,
    Start,
    Stop
}

/// <summary>Only explicit user configuration is executable; paths are passed as environment values.</summary>
public sealed record HomeScriptCommand(string Id, string Name, string Command,
    string Shell = "/bin/zsh", string WorkingDirectory = "")
{
    public HomeScriptMenuIcon MenuIcon { get; init; } = HomeScriptMenuIcon.None;

    public static HomeScriptCommand Create(string path) => new(Guid.NewGuid().ToString("N"), "运行", DefaultCommand(path));

    public static bool IsScript(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".sh" or ".bash" or ".zsh" or ".command" or ".py" or ".js" or ".mjs" or ".cjs"
        or ".scpt" or ".applescript" or ".ts" or ".rb" or ".pl" or ".php" or ".ps1" or ".lua" or ".fish";

    public static string DefaultCommand(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".scpt" or ".applescript" => "osascript \"$SCRIPT\"",
        ".py" => "python3 \"$SCRIPT\"",
        ".js" or ".mjs" or ".cjs" => "node \"$SCRIPT\"",
        ".ts" => "tsx \"$SCRIPT\"",
        ".rb" => "ruby \"$SCRIPT\"",
        ".pl" => "perl \"$SCRIPT\"",
        ".php" => "php \"$SCRIPT\"",
        ".ps1" => "pwsh -File \"$SCRIPT\"",
        ".lua" => "lua \"$SCRIPT\"",
        ".fish" => "fish \"$SCRIPT\"",
        ".bash" => "/bin/bash \"$SCRIPT\"",
        ".zsh" or ".command" => "/bin/zsh \"$SCRIPT\"",
        _ => "/bin/sh \"$SCRIPT\""
    };

    public HomeScriptCommand Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new ArgumentException("命令名称不能为空，且不能超过 80 个字符。");
        if (string.IsNullOrWhiteSpace(Command) || Command.Length > 16384 || Command.Contains('\0'))
            throw new ArgumentException("请输入有效的命令（最多 16384 个字符）。");
        if (Shell is not ("/bin/zsh" or "/bin/bash" or "/bin/sh"))
            throw new ArgumentException("请选择 zsh、bash 或 sh。");
        return this with { Name = Name.Trim(), WorkingDirectory = WorkingDirectory.Trim() };
    }
}

public static class HomeCollectionViewModes
{
    public const string List = "list";
    public const string Grid = "grid";
}

public enum HomeInboxFilter
{
    All,
    Downloads,
    Edited
}

public sealed record HomeSectionPreferences(
    bool ShowStorage = true,
    bool ShowWorkspaces = true,
    bool ShowInbox = true,
    bool ShowCollections = true,
    string CollectionViewMode = HomeCollectionViewModes.List)
{
    public HomeSectionPreferences Normalize() => this with
    {
        CollectionViewMode = string.Equals(CollectionViewMode, HomeCollectionViewModes.Grid, StringComparison.OrdinalIgnoreCase)
            ? HomeCollectionViewModes.Grid
            : HomeCollectionViewModes.List
    };
}

public sealed record HomeRecentItem(
    string Path,
    bool IsDirectory,
    DateTime TimestampUtc,
    bool IsFromDownloads);

public static class HomePathFormatting
{
    public static string FormatParentPath(string fullPath, string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return "";
        var normalized = TrimTrailingSeparator(fullPath);
        var parent = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(parent)) return "/";
        parent = TrimTrailingSeparator(parent);
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            var home = TrimTrailingSeparator(homeDirectory);
            if (string.Equals(parent, home, StringComparison.Ordinal))
                return "~";
            if (parent.StartsWith(home + "/", StringComparison.Ordinal))
                return "~" + parent[home.Length..];
        }
        return parent;
    }

    public static bool IsDefaultSidebarLocation(string path, string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = TrimTrailingSeparator(path);
        if (normalized is "/" or "/Applications") return true;
        if (string.IsNullOrWhiteSpace(homeDirectory)) return false;
        var home = TrimTrailingSeparator(homeDirectory);
        if (string.Equals(normalized, home, StringComparison.Ordinal)) return true;
        return string.Equals(normalized, home + "/Desktop", StringComparison.Ordinal)
            || string.Equals(normalized, home + "/Documents", StringComparison.Ordinal)
            || string.Equals(normalized, home + "/Downloads", StringComparison.Ordinal)
            || string.Equals(normalized, home + "/Pictures", StringComparison.Ordinal)
            || string.Equals(normalized, home + "/Music", StringComparison.Ordinal);
    }

    public static bool IsInsideDirectory(string path, string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directoryPath)) return false;
        var normalizedPath = TrimTrailingSeparator(path);
        var normalizedDir = TrimTrailingSeparator(directoryPath);
        return normalizedPath.StartsWith(normalizedDir + "/", StringComparison.Ordinal);
    }

    public static bool IsSystemTemporaryPath(string path, string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsInsideDirectory(path, homeDirectory)) return false;
        return path.StartsWith("/var/folders/", StringComparison.Ordinal)
            || path.StartsWith("/private/var/folders/", StringComparison.Ordinal)
            || path.StartsWith("/tmp/", StringComparison.Ordinal)
            || path.StartsWith("/private/tmp/", StringComparison.Ordinal);
    }

    private static string TrimTrailingSeparator(string path)
        => path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
