using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "UnifiedFileTag")]
public sealed class TagClipboardNativeTests
{
    [Theory(Explicit = true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task PhysicalPastePreservesCopyTagsAndRemovesOnlyCutSource(bool cut, bool folder, bool fromTag)
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS native metadata.");
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("tag-native-paste-");
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "source")).FullName;
            var targetParent = Directory.CreateDirectory(Path.Combine(root.FullName, "B folder")).FullName;
            var target = Directory.CreateDirectory(Path.Combine(targetParent, "child")).FullName;
            var source = Path.Combine(sourceDirectory, folder ? "folder" : "中文 文件.txt");
            if (folder) Directory.CreateDirectory(source);
            var content = folder ? Path.Combine(source, "child.txt") : source;
            await File.WriteAllTextAsync(content, "original contents", ct);
            var store = new MacFileTagStore();
            using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")), store: store);
            var a = await tags.CreateTagAsync("A", ct);
            var b = await tags.CreateTagAsync("B", ct);
            var c = await tags.CreateTagAsync("C", ct);
            await tags.SetTagAsync([source, content], a, true, ct);
            await tags.SetTagAsync([source, content], c, true, ct);
            await tags.SetTagAsync([targetParent], b, true, ct);
            var clipboard = new ImageClipboardService();
            if (cut) clipboard.CutFiles([source], fromTag ? a : null); else clipboard.CopyFiles([source]);
            var operations = new FileOpsViewModel(clipboard, new MacFileService(), fileTagService: tags);
            await operations.PasteAsync(target);
            var destination = Path.Combine(target, Path.GetFileName(source));
            Assert.Equal(cut && fromTag ? new[] { "C" } : new[] { "A", "C" },
                (await store.ReadAsync(destination, ct)).Select(t => t.Name).Order());
            var destinationContent = folder ? Path.Combine(destination, "child.txt") : destination;
            Assert.Equal("original contents", await File.ReadAllTextAsync(destinationContent, ct));
            if (folder)
                Assert.Equal(new[] { "A", "C" }, (await store.ReadAsync(destinationContent, ct)).Select(t => t.Name).Order());
            if (cut)
            {
                Assert.False(File.Exists(source) || Directory.Exists(source));
                Assert.Null(clipboard.GetClipboardEntry());
            }
            else
            {
                Assert.Equal(new[] { "A", "C" }, (await store.ReadAsync(source, ct)).Select(t => t.Name).Order());
                await tags.SetTagAsync([destination], a, false, ct);
                Assert.DoesNotContain(destination, await tags.FindFilePathsAsync(a, ct));
                Assert.Equal(new[] { "C" }, (await store.ReadAsync(destination, ct)).Select(t => t.Name));
            }
        }
        finally { root.Delete(true); }
    }
}
