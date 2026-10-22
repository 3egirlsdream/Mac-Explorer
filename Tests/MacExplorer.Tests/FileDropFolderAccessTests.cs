using System.Text.Json;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileDrop")]
public sealed class FileDropFolderAccessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("drop-folder-access-").FullName;
    private string Store => Path.Combine(_root, "grants.json");
    private sealed class Bookmarks : IBookmarkAccess
    {
        internal Dictionary<string, string> Paths { get; } = [];
        internal List<nint> Closed { get; } = [];
        private int _next;
        public string Create(string path, bool explicitScope) => throw new NotSupportedException();
        public (nint Scope, string? Path, string? Refreshed, bool Stale) Open(string bookmark)
            => Paths.TryGetValue(bookmark, out var path) ? (++_next, path, null, false) : (0, null, null, false);
        public void Close(nint scope) => Closed.Add(scope);
        public string RealPath(string path) => Path.GetFullPath(path);
    }

    [Theory]
    [InlineData("/temp/drag/file.zip")]
    [InlineData("/temp/RWTemp/2026-08/account/file.png")]
    public void WeChatUsesOneAttachmentCacheFolderForFilesAndImages(string suffix)
    {
        const string account = "/Users/example/Library/Containers/com.tencent.xinWeChat/Data/Documents/xwechat_files/account";
        Assert.Equal(account + "/temp", FileDropFolderAccess.SuggestedFolder(account + suffix));
        Assert.Equal("/other/source", FileDropFolderAccess.SuggestedFolder("/other/source/file.zip"));
    }

    [Fact]
    public void SelectedFolderCoversDescendantsButNotSiblingPrefixes()
    {
        var backend = new Bookmarks(); backend.Paths["selected"] = Path.Combine(_root, "attachments");
        using var grants = new FileDropFolderAccess(Store, backend);
        Assert.True(grants.Remember("selected", Path.Combine(_root, "attachments", "file.zip")));
        Assert.True(grants.Covers(Path.Combine(_root, "attachments", "images", "file.png")));
        Assert.False(grants.Covers(Path.Combine(_root, "attachments-other", "file.png")));
        Assert.True(FileDropFolderAccess.IsWithin("/source/file", "/"));
    }

    [Fact]
    public void UnrelatedSelectionIsRejectedAndItsScopeIsClosed()
    {
        var backend = new Bookmarks(); backend.Paths["wrong"] = Path.Combine(_root, "other");
        using var grants = new FileDropFolderAccess(Store, backend);
        Assert.False(grants.Remember("wrong", Path.Combine(_root, "source", "file.zip")));
        Assert.Single(backend.Closed);
        Assert.False(File.Exists(Store));
    }

    [Fact]
    public void RestartRestoresSelectedFolderWithoutRequestingAnotherGrant()
    {
        var backend = new Bookmarks(); backend.Paths["selected"] = _root;
        using (var grants = new FileDropFolderAccess(Store, backend))
            Assert.True(grants.Remember("selected", Path.Combine(_root, "file.zip")));
        Assert.Single(backend.Closed);
        using (var restarted = new FileDropFolderAccess(Store, backend))
            Assert.True(restarted.Covers(Path.Combine(_root, "another", "file.png")));
        Assert.Equal(2, backend.Closed.Count);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Store));
    }

    [Fact]
    public void ExpiredBookmarksDoNotClaimAccess()
    {
        File.WriteAllText(Store, JsonSerializer.Serialize(new Dictionary<string, string> { [_root] = "expired" }));
        using var grants = new FileDropFolderAccess(Store, new Bookmarks());
        Assert.False(grants.Covers(Path.Combine(_root, "file.zip")));
    }

    [Fact]
    public void CorruptStoreDoesNotPreventThisSessionOrOverwriteTheOriginal()
    {
        File.WriteAllText(Store, "invalid-json");
        var backend = new Bookmarks(); backend.Paths["selected"] = _root;
        using var grants = new FileDropFolderAccess(Store, backend);
        Assert.True(grants.Remember("selected", Path.Combine(_root, "file.zip")));
        Assert.True(grants.Covers(Path.Combine(_root, "file.png")));
        Assert.NotNull(grants.PersistenceError);
        Assert.Equal("invalid-json", File.ReadAllText(Store));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
