namespace MacExplorer;

/// <summary>Explicit test-profile paths; ordinary launches keep their existing locations.</summary>
internal static class RuntimePaths
{
    public const string TestRootVariable = "MACEXPLORER_TEST_ROOT";

    public static string? TestRoot
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(TestRootVariable);
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!Path.IsPathFullyQualified(value))
                throw new ArgumentException($"{TestRootVariable} must be an absolute path.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
    }

    public static string HomeDirectory => TestRoot
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string LocalApplicationData => TestRoot is { } root
        ? Path.Combine(root, ".macexplorer")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Test isolation takes precedence over an inherited production DB override.
    public static string DatabasePath => TestRoot is { } root
        ? Path.Combine(root, ".macexplorer", "index.db")
        : Environment.GetEnvironmentVariable("MACEXPLORER_DB_PATH")
          ?? Path.Combine(HomeDirectory, "Documents", "MacExplorer", "index.db");

    public static IReadOnlyList<string> StartupIndexRoots => TestRoot is { } root
        ? [root]
        : ["/Applications", "/System/Applications", HomeDirectory];

    public static void PrepareTestRoot()
    {
        if (TestRoot is not { } root) return;
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Movies" })
            Directory.CreateDirectory(Path.Combine(root, name));
    }

    public static string ResolveSearchRoot(string directory)
    {
        if (TestRoot is not { } root) return directory;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (IsWithin(path, root)) return path;
        // “This Mac” remains useful in tests, but covers only the fixture tree.
        if (IsWithin(root, path)) return root;
        throw new ArgumentException("Test searches must stay inside MACEXPLORER_TEST_ROOT.", nameof(directory));
    }

    private static bool IsWithin(string path, string root) => path == root
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
}
