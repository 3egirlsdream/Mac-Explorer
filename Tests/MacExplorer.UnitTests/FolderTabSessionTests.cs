using MacExplorer.Services;
using Xunit;

namespace MacExplorer.UnitTests;

[Trait("Kind", "Unit")]
[Trait("Area", "NavigationSettings")]
public sealed class FolderTabSessionTests
{
    [Fact]
    public void DisabledByDefaultDoesNotReadOrWriteSession()
    {
        var settings = new MemorySettings();
        settings.Set(FolderTabSession.SessionKey, "old session");
        var session = FolderTabSession.Capture(["/first"], 0);
        session.Save(settings);
        Assert.Equal("old session", settings.Get(FolderTabSession.SessionKey));
        Assert.Null(FolderTabSession.Read(settings, _ => throw new Exception("Must not check paths when disabled")));
    }

    [Fact]
    public void RoundTripPreservesOrderDuplicatesAndSelectedIndex()
    {
        var settings = EnabledSettings();
        FolderTabSession.Capture(["/first", "/second", "/first"], 2).Save(settings);
        var restored = FolderTabSession.Read(settings, _ => true);
        Assert.NotNull(restored);
        Assert.Equal(new[] { "/first", "/second", "/first" }, restored.Paths);
        Assert.Equal(2, restored.SelectedIndex);
    }

    [Fact]
    public void FilteringVirtualAndUnavailableTabsRemapsSelection()
    {
        var settings = EnabledSettings();
        FolderTabSession.Capture([null, "home://", "/missing", "/first", "relative", "/selected"], 5).Save(settings);
        var restored = FolderTabSession.Read(settings, path => path != "/missing");
        Assert.NotNull(restored);
        Assert.Equal(new[] { "/first", "/selected" }, restored.Paths);
        Assert.Equal(1, restored.SelectedIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(1)]
    public void InvalidOrRemovedSelectionFallsBackToFirstFolder(int selected)
    {
        var settings = EnabledSettings();
        new FolderTabSession(["/first", "/missing", "/third"], selected).Save(settings);
        Assert.Equal(0, FolderTabSession.Read(settings, path => path != "/missing")!.SelectedIndex);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Paths\":null}")]
    [InlineData("{\"Paths\":42}")]
    [InlineData("{\"Paths\":[]}")]
    [InlineData("{\"Paths\":[null,\"relative\",\"home://\",\"/bad\\u0000path\"]}")]
    public void InvalidOrEmptySavedSessionUsesLegacyFallback(string json)
    {
        var settings = EnabledSettings();
        settings.Set(FolderTabSession.SessionKey, json);
        Assert.Null(FolderTabSession.Read(settings, _ => true));
    }

    [Fact]
    public void MissingFilesAndAccessErrorsAreSkipped()
    {
        var settings = EnabledSettings();
        FolderTabSession.Capture(["/missing", "/denied", "/io-error", "/valid"], 3).Save(settings);
        var restored = FolderTabSession.Read(settings, path => path switch
        {
            "/missing" => false,
            "/denied" => throw new UnauthorizedAccessException(),
            "/io-error" => throw new IOException(),
            _ => true
        });
        Assert.Equal(new[] { "/valid" }, restored!.Paths);
        Assert.Equal(0, restored.SelectedIndex);
        Assert.Null(FolderTabSession.Read(settings, _ => false));
    }

    [Fact]
    public void RealDirectoryValidationSkipsFilesAndDeletedFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "tab-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "file.txt");
            File.WriteAllText(file, "test");
            var settings = EnabledSettings();
            FolderTabSession.Capture([file, Path.Combine(root, "missing"), root], 2).Save(settings);
            var restored = FolderTabSession.Read(settings, Directory.Exists);
            Assert.Equal(new[] { root }, restored!.Paths);
            Assert.Equal(0, restored.SelectedIndex);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WindowRoundTripPreservesBoundariesDuplicatesAndEachSelection()
    {
        var settings = EnabledSettings();
        FolderWindowSession.Capture([
            FolderTabSession.Capture(["/a", "/b", "/a"], 2),
            FolderTabSession.Capture(["/c", "/d"], 1)], 1).Save(settings);
        var restored = FolderWindowSession.Read(settings, _ => true)!;
        Assert.Equal(2, restored.Windows.Length);
        Assert.Equal(new[] { "/a", "/b", "/a" }, restored.Windows[0].Paths);
        Assert.Equal(2, restored.Windows[0].SelectedIndex);
        Assert.Equal(new[] { "/c", "/d" }, restored.Windows[1].Paths);
        Assert.Equal(1, restored.Windows[1].SelectedIndex);
        Assert.Equal(1, restored.SelectedWindowIndex);
    }

    [Fact]
    public void MissingWindowsAndFoldersRemapBothSelections()
    {
        var settings = EnabledSettings();
        new FolderWindowSession([
            new(["/missing"], 0), new(["/missing", "/a", "/b"], 2)], 1).Save(settings);
        var restored = FolderWindowSession.Read(settings, path => path != "/missing")!;
        Assert.Single(restored.Windows);
        Assert.Equal(0, restored.SelectedWindowIndex);
        Assert.Equal(1, restored.Windows[0].SelectedIndex);
        Assert.Null(FolderWindowSession.Read(settings, _ => false));
    }

    [Fact]
    public void OldSingleWindowSessionMigratesAndLastWindowReplacesMultipleWindows()
    {
        var settings = EnabledSettings();
        FolderTabSession.Capture(["/old", "/selected"], 1).Save(settings);
        var migrated = FolderWindowSession.Read(settings, _ => true)!;
        Assert.Equal(1, Assert.Single(migrated.Windows).SelectedIndex);
        FolderWindowSession.Capture([new(["/one"], 0), new(["/two"], 0)], 1).Save(settings);
        FolderWindowSession.Capture([new(["/last", "/last"], 1)], 0).Save(settings);
        var restored = FolderWindowSession.Read(settings, _ => true)!;
        Assert.Equal(new[] { "/last", "/last" }, Assert.Single(restored.Windows).Paths);
        Assert.Equal(1, restored.Windows[0].SelectedIndex);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Windows\":null}")]
    [InlineData("{\"Windows\":[null,{\"Paths\":null}]}")]
    [InlineData("{\"Windows\":42}")]
    public void InvalidWindowSessionFallsBack(string json)
    {
        var settings = EnabledSettings();
        settings.Set(FolderTabSession.SessionKey, json);
        Assert.Null(FolderWindowSession.Read(settings, _ => true));
    }

    private static MemorySettings EnabledSettings()
    {
        var settings = new MemorySettings();
        settings.Set(FolderTabSession.EnabledKey, true);
        return settings;
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = [];
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue)
            => _values.TryGetValue(key, out var value) ? (T)Convert.ChangeType(value, typeof(T)) : defaultValue;
        public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }
}
