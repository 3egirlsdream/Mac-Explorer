using Avalonia.Headless.XUnit;
using MacExplorer.Models;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData("image.webp")]
    [InlineData("image.WEBP")]
    public async Task WebpConversionMenuAppearsInBothBuildPhases(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "macexplorer-webp-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var plugins = new PluginTestEnvironment();
            var entry = new FileSystemEntry
            {
                FullPath = Path.Combine(root, name), Name = name, Extension = Path.GetExtension(name)
            };
            var files = new FakeFileService(root);
            files.Seed(entry);
            using var vm = CreateViewModel(files, pluginManager: plugins.Manager);
            await vm.RefreshAsync();
            vm.SetSelection([entry]);
            await vm.ShowFileContextMenuAsync(entry, 0, 0);
            var fast = Assert.Single(vm.ContextMenuActions, item => item.Label == "文件转换");
            var complete = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(entry), item => item.Label == "文件转换");
            Assert.Equal(new[] { "转为 PNG", "转为 JPG" }, fast.SubItems!.Select(item => item.Label));
            Assert.Equal(fast.SubItems!.Select(item => item.Label), complete.SubItems!.Select(item => item.Label));
        }
        finally { Directory.Delete(root, true); }
    }
}
