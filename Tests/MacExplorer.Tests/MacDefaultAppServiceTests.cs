using System.Xml.Linq;
using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

public class MacDefaultAppServiceTests
{
    private const string AppId = "com.macexplorer.app";
    private const string FinderId = "com.apple.finder";

    [Fact]
    public void FolderPreferenceUpdateReplacesDuplicatesAndPreservesOtherAssociations()
    {
        var handlers = XElement.Parse("""
            <array>
              <dict><key>LSHandlerContentType</key><string>public.plain-text</string><key>LSHandlerRoleAll</key><string>com.other.editor</string><key>LSHandlerPreferredVersions</key><dict><key>LSHandlerRoleAll</key><string>-</string></dict></dict>
              <dict><key>LSHandlerContentType</key><string>public.folder</string><key>LSHandlerRoleAll</key><string>com.apple.finder</string></dict>
              <dict><key>LSHandlerURLScheme</key><string>https</string><key>LSHandlerRoleAll</key><string>com.other.browser</string></dict>
              <dict><key>LSHandlerContentType</key><string>public.folder</string><key>LSHandlerRoleViewer</key><string>com.old.viewer</string></dict>
            </array>
            """);
        var original = new XElement(handlers);

        var updated = MacDefaultAppService.UpdateFolderHandlers(handlers, AppId);

        Assert.True(XNode.DeepEquals(original, handlers));
        Assert.Equal(3, updated.Elements().Count());
        Assert.True(XNode.DeepEquals(original.Elements().First(), updated.Elements().First()));
        Assert.True(XNode.DeepEquals(original.Elements().ElementAt(2), updated.Elements().ElementAt(1)));
        Assert.Equal(AppId, MacDefaultAppService.GetSavedFolderHandler(updated));
        Assert.True(XNode.DeepEquals(updated, MacDefaultAppService.UpdateFolderHandlers(updated, AppId)));
    }

    [Fact]
    public void SavedFolderPreferenceCanBeEnabledAndRestoredBeforeSystemCacheRefreshes()
    {
        var handlers = new XElement("array");
        Assert.Null(MacDefaultAppService.GetSavedFolderHandler(handlers));

        handlers = MacDefaultAppService.UpdateFolderHandlers(handlers, AppId);
        Assert.Equal(AppId, MacDefaultAppService.GetSavedFolderHandler(handlers));

        handlers = MacDefaultAppService.UpdateFolderHandlers(handlers, FinderId);
        Assert.Equal(FinderId, MacDefaultAppService.GetSavedFolderHandler(handlers));
        Assert.Single(handlers.Elements());
    }

    [Theory]
    [InlineData(AppId, null, false)]
    [InlineData(FinderId, AppId, false)]
    [InlineData(AppId, "com.other.viewer", false)]
    [InlineData(AppId, AppId, true)]
    public void DefaultStatusRequiresBothSystemPreferences(string folder, string? viewer, bool expected)
    {
        var service = new FakeService { Folder = folder, Viewer = viewer };

        Assert.Equal(expected, service.IsDefaultFolderHandler());
    }

    [Fact]
    public void EnableSetsBothPreferencesAndRequestsComputerRestart()
    {
        var service = new FakeService();

        var result = service.SetAsDefaultFolderHandler();

        Assert.True(result.Success, result.Message);
        Assert.True(service.IsDefaultFolderHandler());
        Assert.Contains("请重启电脑", result.Message);
    }

    [Fact]
    public void DisableRestoresFinderAndRemovesFileViewerOverride()
    {
        var service = new FakeService { Folder = AppId, Viewer = AppId };

        var result = service.ResetDefaultFolderHandler();

        Assert.True(result.Success, result.Message);
        Assert.Equal(FinderId, service.Folder);
        Assert.Null(service.Viewer);
        Assert.False(service.IsDefaultFolderHandler());
        Assert.Contains("请重启电脑", result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrIgnoredViewerWriteRestoresPreviousPreferences(bool ignoreWrite)
    {
        var service = new FakeService
        {
            Folder = "com.other.manager", Viewer = "com.other.viewer",
            FailViewerWrite = !ignoreWrite, IgnoreViewerWrite = ignoreWrite
        };

        var result = service.SetAsDefaultFolderHandler();

        Assert.False(result.Success);
        Assert.Equal("com.other.manager", service.Folder);
        Assert.Equal("com.other.viewer", service.Viewer);
        Assert.DoesNotContain("请重启电脑", result.Message);
    }

    [Fact]
    public void FailedFolderWriteDoesNotLeaveViewerOverrideEnabled()
    {
        var service = new FakeService { FailFolderWrite = true };

        var result = service.SetAsDefaultFolderHandler();

        Assert.False(result.Success);
        Assert.Equal(FinderId, service.Folder);
        Assert.Null(service.Viewer);
    }

    private sealed class FakeService : MacDefaultAppService
    {
        internal string Folder = FinderId;
        internal string? Viewer;
        internal bool FailFolderWrite;
        internal bool FailViewerWrite;
        internal bool IgnoreViewerWrite;

        internal override string? ReadFolderHandler() => Folder;
        internal override string? ReadFileViewer() => Viewer;

        internal override void WriteFolderHandler(string bundleId)
        {
            if (bundleId == AppId && FailFolderWrite)
                throw new InvalidOperationException("Folder write failed");
            Folder = bundleId;
        }

        internal override void WriteFileViewer(string? bundleId)
        {
            if (bundleId == AppId && FailViewerWrite)
                throw new InvalidOperationException("Viewer write failed");
            if (bundleId == AppId && IgnoreViewerWrite) return;
            Viewer = bundleId;
        }
    }
}
