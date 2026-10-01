namespace MacExplorer;

/// <summary>Navigation paths and application storage are deliberately separate.</summary>
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

    public static string HomeDirectory => TestRoot ?? (OperatingSystem.IsMacOS()
        ? Platforms.MacOS.MacSandboxNative.UserHome
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string LocalApplicationData => TestRoot is { } root
        ? Path.Combine(root, ".macexplorer")
        : OperatingSystem.IsMacOS() ? Platforms.MacOS.MacSandboxNative.StoragePath(false)
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataDirectory => Path.Combine(LocalApplicationData, "MacExplorer");
    public static string CacheDirectory => TestRoot is { } root
        ? Path.Combine(root, ".macexplorer", "Caches")
        : Path.Combine(OperatingSystem.IsMacOS() ? Platforms.MacOS.MacSandboxNative.StoragePath(true)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacExplorer");
    public static string BundleExecutableDirectory => Directory.Exists(Path.Combine(AppContext.BaseDirectory, "..", "..", "MacOS"))
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "MacOS")) : AppContext.BaseDirectory;
    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");
    public static string TemporaryDirectory => OperatingSystem.IsMacOS()
        ? Platforms.MacOS.MacSandboxNative.TemporaryDirectory : Path.GetTempPath();

    // Test isolation takes precedence over an inherited production DB override.
    public static string DatabasePath => TestRoot is { } root
        ? Path.Combine(root, ".macexplorer", "index.db")
        : (!DistributionChannel.IsAppStore ? Environment.GetEnvironmentVariable("MACEXPLORER_DB_PATH") : null)
          ?? Path.Combine(DataDirectory, "index.db");

    public static IReadOnlyList<string> StartupIndexRoots => TestRoot is { } root
        ? [root]
        : DistributionChannel.IsAppStore ? Services.DirectoryAccess.Current.AuthorizedRoots
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
        if (TestRoot is not { } root)
        {
            if (DistributionChannel.IsAppStore && directory != "/")
                Services.DirectoryAccess.Current.EnsureAccess(directory);
            return directory;
        }
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
