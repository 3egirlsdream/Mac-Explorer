namespace FKFinder.Models;

public class ContextMenuAction
{
    public string Label { get; init; } = string.Empty;
    public string IconSvg { get; init; } = string.Empty;
    public string ShortcutText { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public bool IsSeparator { get; init; }
    public Func<Task>? Execute { get; init; }
    public IReadOnlyList<ContextMenuAction>? SubItems { get; init; }
    public string? Tag { get; init; }
    public bool IsQuickAction { get; init; }

    public static ContextMenuAction Separator => new() { IsSeparator = true };
}

/// <summary>
/// SVG icon constants for the context menu and UI.
/// Simple, flat, modern icons.
/// </summary>
public static class Icons
{
    // Navigation
    public const string Back = "M15 19l-7-7 7-7";
    public const string Forward = "M9 5l7 7-7 7";
    public const string Up = "M12 19V5M5 12l7-7 7 7";
    public const string Refresh = "M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z";
    public const string Home = "M3 9l9-7 9 7v11a2 2 0 01-2 2H5a2 2 0 01-2-2zM9 22V12h6v10";

    // File operations
    public const string NewFolder = "M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2z";
    public const string NewFile = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8zM14 2v6h6M12 18v-6M9 15h6";
    public const string Open = "M5 19a2 2 0 01-2-2V7a2 2 0 012-2h4l2 2h4a2 2 0 012 2v1M5 19h14a2 2 0 002-2v-5a2 2 0 00-2-2H9a2 2 0 00-2 2v5a2 2 0 01-2 2z";
    public const string Copy = "M20 9h-9a2 2 0 00-2 2v9a2 2 0 002 2h9a2 2 0 002-2v-9a2 2 0 00-2-2zM5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1";
    public const string Cut = "M9 6a3 3 0 11-6 0 3 3 0 016 0zM9 18a3 3 0 11-6 0 3 3 0 016 0zM20 4L8.12 15.88M14.47 14.48L20 20M8.12 8.12L12 12";
    public const string Paste = "M16 4h2a2 2 0 012 2v14a2 2 0 01-2 2H6a2 2 0 01-2-2V6a2 2 0 012-2h2M9 2h6a1 1 0 011 1v2a1 1 0 01-1 1H9a1 1 0 01-1-1V3a1 1 0 011-1zM9 10h6M9 14h6";
    public const string Delete = "M3 6h18M19 6v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6m3 0V4a2 2 0 012-2h4a2 2 0 012 2v2M10 11v6M14 11v6";
    public const string Rename = "M11 4H4a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2v-7M18.5 2.5a2.121 2.121 0 013 3L12 15l-4 1 1-4 9.5-9.5z";

    // View (Fluent Design — rounded, balanced at small sizes)
    public const string Grid = "M3 3h7a1 1 0 011 1v6a1 1 0 01-1 1H3a1 1 0 01-1-1V4a1 1 0 011-1zM14 3h7a1 1 0 011 1v6a1 1 0 01-1 1h-7a1 1 0 01-1-1V4a1 1 0 011-1zM3 14h7a1 1 0 011 1v6a1 1 0 01-1 1H3a1 1 0 01-1-1v-6a1 1 0 011-1zM14 14h7a1 1 0 011 1v6a1 1 0 01-1 1h-7a1 1 0 01-1-1v-6a1 1 0 011-1z";
    public const string List = "M3 5.5h2a1 1 0 011 1v0a1 1 0 01-1 1H3a1 1 0 01-1-1v0a1 1 0 011-1zM3 11h2a1 1 0 011 1v0a1 1 0 01-1 1H3a1 1 0 01-1-1v0a1 1 0 011-1zM3 16.5h2a1 1 0 011 1v0a1 1 0 01-1 1H3a1 1 0 01-1-1v0a1 1 0 011-1zM9 6.5h13M9 12h13M9 17.5h13";
    public const string Sort = "M3 6h18M3 12h12M3 18h6";
    public const string SortAsc = "M12 5v14M5 12l7-7 7 7";
    public const string SortDesc = "M12 19V5M5 12l7 7 7-7";
    public const string SortUpDown = "M8 4v16M4 8l4-4 4 4M16 20V4M12 16l4 4 4-4";

    // Info
    public const string Info = "M12 22C6.477 22 2 17.523 2 12S6.477 2 12 2s10 4.477 10 10-4.477 10-10 10zM12 16v-4M12 8h.01";
    public const string CopyPath = "M10 13a5 5 0 007.54.54l3-3a5 5 0 00-7.07-7.07l-1.72 1.71M14 11a5 5 0 00-7.54-.54l-3 3a5 5 0 007.07 7.07l1.71-1.71";

    // Apps
    public const string Terminal = "M4 17l6-5-6-5M12 19h8";
    public const string Finder = "M4 2h16a2 2 0 012 2v16a2 2 0 01-2 2H4a2 2 0 01-2-2V4a2 2 0 012-2zM12 2v13M8.5 9.5h.01M15.5 9.5h.01M7.5 16c1.5 2 3 2.5 4.5 2.5s3-.5 4.5-2.5";
    public const string Search = "M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z";
    public const string VSCode = "M23.15 2.587L18.21.21a1.494 1.494 0 00-1.705.29l-6.046 5.503-3.27-2.517a.998.998 0 00-1.289.072l-1.7 1.7a.999.999 0 000 1.414l2.825 2.825-2.825 2.825a.999.999 0 000 1.414l1.7 1.7c.36.36.926.388 1.29.072l3.269-2.517 6.046 5.503a1.494 1.494 0 001.705.29l4.94-2.377A1.5 1.5 0 0024 18.41V5.59a1.5 1.5 0 00-.85-1.003zM17.5 18.5l-7-5.5 7-5.5v11z";

    /// <summary>
    /// Returns true if the icon should be rendered with fill instead of stroke.
    /// </summary>
    public static bool IsFillIcon(string? iconSvg) => iconSvg == VSCode || iconSvg == StarFilled;

    // Add
    public const string Plus = "M12 5v14M5 12h14";
    public const string Close = "M18 6L6 18M6 6l12 12";

    // Star ratings
    public const string StarFilled = "M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z";
    public const string StarOutline = "M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z";

    // Collections / Preview
    public const string CollectionAdd = "M19 21l-7-5-7 5V5a2 2 0 012-2h10a2 2 0 012 2z";
    public const string Preview = "M3 4a2 2 0 012-2h14a2 2 0 012 2v16a2 2 0 01-2 2H5a2 2 0 01-2-2V4zM14.5 2v20";

    // Archive operations
    public const string Extract = "M12 3v12m0 0l-4-4m4 4l4-4M4 15v2a2 2 0 002 2h12a2 2 0 002-2v-2";
    public const string Compress = "M12 21V9m0 0l4 4m-4-4l-4 4M4 9V7a2 2 0 012-2h12a2 2 0 012 2v2";

    // Folder/File type icons (simplified SVG paths for 24x24 viewBox)
    public const string Folder = "M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z";
    public const string File = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6";
    public const string FileText = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M16 13H8M16 17H8M10 9H8";
    public const string FileCode = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M10 13l-2 2 2 2M14 13l2 2-2 2";
    public const string FileImage = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M8 16l3-4 2 3 3-4 4 5H8z";
    public const string FileArchive = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M10 12v2M10 16v2M12 12h2v2h-2z";
    public const string FileVideo = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M10 11v6l5-3-5-3z";
    public const string FileAudio = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M11 14a2 2 0 11-4 0 2 2 0 014 0zM11 14V10l4-1";
    public const string FilePdf = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M8 13h2a1 1 0 001-1v-1a1 1 0 00-1-1H8v5";
    public const string FileSpreadsheet = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M8 13h8M8 17h8M12 10v10";
    public const string FilePresentation = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M7 11h10v7H7z";
    public const string FileMarkdown = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M7 13v5l2.5-2.5L12 18v-5M15 13v5";
    public const string FileConfig = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M12 18a2 2 0 100-4 2 2 0 000 4zM12 12v2";

    // Quick access / Frequent
    public const string QuickAccess = "M13 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V9l-7-7zM13 2v7h7M9 13l2 2 4-4";
    public const string FrequentFolder = "M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2zM12 11v4M10 13h4";
    public const string NewWindow = "M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7";
}
