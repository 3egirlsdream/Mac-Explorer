namespace MacExplorer.Models;

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
    public const string Info = "M12 3a9 9 0 100 18 9 9 0 100-18zM12 7v6M12 17h.01";
    public const string CopyPath = "M10 13a5 5 0 007.54.54l3-3a5 5 0 00-7.07-7.07l-1.72 1.71M14 11a5 5 0 00-7.54-.54l-3 3a5 5 0 007.07 7.07l1.71-1.71";

    // Apps
    public const string Terminal = "M4 17l6-5-6-5M12 19h8";
    public const string Finder = "M12 2v14M7 10h.01M17 10h.01M5 18c2.3 2.5 4.7 3.5 7 3.5s4.7-1 7-3.5";
    public const string Search = "M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z";
    public const string VSCode = "M16.2 3.1L15.4 3.4L9.4 10L5.6 6.9L4.8 6.8L3.2 7.5L3 8L3 16L3.2 16.5L4.8 17.3L5.4 17.3L9.4 14L15.8 20.9L16.7 20.9L20.7 18.8L21 18.1L21 5.9L20.3 5L16.2 3.1zM16.5 8.2L16.5 15.8L11.9 12L16.5 8.2zM5.3 9.5L7.6 12L5.3 14.6L5.3 9.5z";
    public const string CodeEditor = "M7 8l-4 4 4 4M17 8l4 4-4 4M14 4l-4 16";
    public const string Kiro = "M7.09 16.76c-1.89 4.19 2.14 5.24 5.11 2.78.87 2.75 4.15.7 5.32-1.44 2.59-4.69 1.54-9.49 1.28-10.48-1.84-6.74-11.04-6.75-12.63.03-.37 1.18-.38 2.54-.59 3.93-.1.71-.18 1.16-.45 1.9-.16.43-.37.8-.71 1.45-.53.99-.3 2.91 2.42 1.91l0.25-.11h-.01zM12.55 10.56c-.76 0-.87-.91-.87-1.44 0-.48.08-.87.25-1.12.14-.22.36-.32.62-.32.26 0 .49.1.66.33.18.25.28.63.28 1.1 0 .91-.34 1.44-.92 1.44h-.01zM15.66 10.56c-.76 0-.87-.91-.87-1.44 0-.48.08-.87.25-1.12.14-.22.36-.32.62-.32.26 0 .49.1.66.33.18.25.28.63.28 1.1 0 .91-.34 1.44-.92 1.44h-.01z";
    public const string Qoder = "M21 13.8L21 10.7L20.9 9.8L20.1 7.7L12 3.1L13.1 4.6L13.9 6.2L13.9 10.7L13.5 12.1L13.1 13.5L11.3 15.5L9.2 16.8L6.9 17.3L4.7 16.7L9.1 18.9L12.8 21L15 21L17 20.3L17.6 20L20 20.7L21 19.4zM12 3.1L10 3L8.3 3L5 5.1L3.2 8.5L3 10.5L3 13.2L4.4 16.4L12 20.7L10.5 19.1L10 17.6L10 13.1L10.1 11.9L10.7 10.6L12.1 8.6L14.3 7L16.8 6.4L18.9 6.7z";

    /// <summary>
    /// Returns true if the icon should be rendered with fill instead of stroke.
    /// </summary>
    public static bool IsFillIcon(string? iconSvg) => iconSvg == VSCode || iconSvg == StarFilled || iconSvg == Pin || iconSvg == Kiro || iconSvg == Qoder;

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

    // Pin
    public const string Pin = "M12 2a2 2 0 00-2 2v5.07A5.98 5.98 0 006 14v1h5v7l1 1 1-1v-7h5v-1a5.98 5.98 0 00-4-5.07V4a2 2 0 00-2-2z";

    // Quick access / Frequent
    public const string QuickAccess = "M13 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V9l-7-7zM13 2v7h7M9 13l2 2 4-4";
    public const string FrequentFolder = "M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2zM12 11v4M10 13h4";
    public const string NewWindow = "M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7";

    // Settings
    public const string Settings = "M12.22 2h-.44a2 2 0 00-2 2v.18a2 2 0 01-1 1.73l-.43.25a2 2 0 01-2 0l-.15-.08a2 2 0 00-2.73.73l-.22.38a2 2 0 00.73 2.73l.15.1a2 2 0 011 1.72v.51a2 2 0 01-1 1.74l-.15.09a2 2 0 00-.73 2.73l.22.38a2 2 0 002.73.73l.15-.08a2 2 0 012 0l.43.25a2 2 0 011 1.73V20a2 2 0 002 2h.44a2 2 0 002-2v-.18a2 2 0 011-1.73l.43-.25a2 2 0 012 0l.15.08a2 2 0 002.73-.73l.22-.39a2 2 0 00-.73-2.73l-.15-.08a2 2 0 01-1-1.74v-.5a2 2 0 011-1.74l.15-.09a2 2 0 00.73-2.73l-.22-.38a2 2 0 00-2.73-.73l-.15.08a2 2 0 01-2 0l-.43-.25a2 2 0 01-1-1.73V4a2 2 0 00-2-2zM12 15a3 3 0 100-6 3 3 0 000 6z";
}
