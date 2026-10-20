namespace MacExplorer.Services;

internal static class ArchiveFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".cbz", ".jar", ".war", ".ear", ".apk", ".xpi", ".vsix",
        ".rar", ".7z", ".tar", ".gz", ".gzip", ".tgz", ".taz", ".bz2", ".bzip2",
        ".tbz", ".tbz2", ".tb2", ".tz2", ".xz", ".txz", ".zst", ".zstd", ".tzst", ".tzstd",
        ".lz", ".tlz", ".z", ".tz", ".arc", ".arj", ".ace"
    };

    private static readonly string[] CompoundExtensions =
    [
        ".tar.gzip", ".tar.bzip2", ".tar.zstd", ".tar.gz", ".tar.bz2",
        ".tar.xz", ".tar.zst", ".tar.lz", ".tar.Z"
    ];

    internal static bool IsArchive(string path) => Extensions.Contains(Path.GetExtension(path));

    internal static bool IsTarName(string path)
        => CompoundExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            || new[] { ".tar", ".tgz", ".taz", ".tbz", ".tbz2", ".tb2", ".tz2", ".txz", ".tlz", ".tzst", ".tzstd", ".tz" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static string RemoveExtension(string path)
    {
        var name = Path.GetFileName(path);
        var compound = CompoundExtensions.FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        return compound == null ? Path.GetFileNameWithoutExtension(name) : name[..^compound.Length];
    }

    internal static string SingleFileName(string archivePath, string? embeddedName = null)
    {
        // Compression headers can contain a path from another operating system.
        var name = embeddedName?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(name) && !name.Contains('/') && !name.Contains(':')
            && !name.Contains('\0') && name is not "." and not "..")
            return name;
        name = Path.GetFileNameWithoutExtension(archivePath);
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "content" : name;
    }
}
