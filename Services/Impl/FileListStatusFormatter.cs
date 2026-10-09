using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

internal readonly record struct DriveSpaceSnapshot(
    string RootPath,
    long AvailableFreeSpace,
    long TotalSize);

internal readonly record struct LocationStatus(
    string Text,
    string Tooltip);

internal static class FileListStatusFormatter
{
    public static string FormatSelectionSummary(
        IReadOnlyCollection<FileSystemEntry> entries,
        IReadOnlyCollection<FileSystemEntry> selectedEntries)
    {
        if (selectedEntries.Count == 0)
            return LocalizationText.Get("{0} 项", entries.Count);

        if (selectedEntries.Any(entry => entry.IsDirectory))
            return LocalizationText.Get("已选 {0} 项", selectedEntries.Count);

        var selectedBytes = selectedEntries.Sum(entry => Math.Max(0, entry.Size));
        return LocalizationText.Get("已选 {0} 项", selectedEntries.Count) + " · " + FormatBytes(selectedBytes);
    }

    public static LocationStatus? GetLocalLocationStatus(
        string path,
        IEnumerable<DriveSpaceSnapshot> drives)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalizedPath = NormalizePath(path);
        var drive = drives
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.RootPath))
            .Select(candidate => (Snapshot: candidate, Root: NormalizePath(candidate.RootPath)))
            .Where(candidate => IsWithinRoot(normalizedPath, candidate.Root))
            .OrderByDescending(candidate => candidate.Root.Length)
            .Select(candidate => candidate.Snapshot)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(drive.RootPath))
            return null;

        var available = FormatBytes(Math.Max(0, drive.AvailableFreeSpace));
        var total = FormatBytes(Math.Max(0, drive.TotalSize));
        return new LocationStatus(LocalizationText.Get("可用 {0}", available), LocalizationText.Get("{0} 可用，共 {1}", available, total));
    }

    public static LocationStatus GetRemoteLocationStatus(string serverName, bool connected)
    {
        var text = $"{serverName} · {(LocalizationText.Get(connected ? "已连接" : "连接已断开"))}";
        return new LocationStatus(text, text);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString("0.##", LocalizationService.Current?.Culture ?? System.Globalization.CultureInfo.GetCultureInfo("zh-CN")) + " " + units[unit];
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path);
        return normalized == Path.DirectorySeparatorChar.ToString()
            ? normalized
            : normalized.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        if (root == Path.DirectorySeparatorChar.ToString())
            return path.StartsWith(root, StringComparison.Ordinal);

        return string.Equals(path, root, StringComparison.Ordinal)
               || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
