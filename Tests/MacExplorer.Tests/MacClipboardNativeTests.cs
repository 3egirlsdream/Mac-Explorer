using Avalonia.Headless.XUnit;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Clipboard")]
public sealed class MacClipboardNativeTests
{
    [AvaloniaFact(Explicit = true)]
    public async Task ExternalFilesReplaceOldTagCutAndTextInvalidatesAdoptedFiles()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS AppKit.");
        var root = Directory.CreateTempSubdirectory("clipboard-native-");
        try
        {
            var first = Path.Combine(root.FullName, "中文 file #.txt");
            var second = Path.Combine(root.FullName, "second.txt");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
            var name = "MacExplorer.ClipboardTest." + Guid.NewGuid().ToString("N");
            var clipboard = new MacClipboardService(name);
            var external = new MacClipboardService(name);
            var sourceTag = new FileTag("A", "#8E8E93", FileTagKind.Custom);
            clipboard.CutFiles([first], sourceTag);
            Assert.Equal(ClipboardPasteKind.InAppFiles, clipboard.GetPasteKind());
            Assert.Equal(sourceTag, clipboard.GetClipboardEntry()!.SourceTag);
            external.CopyFiles([first, second]);
            Assert.Equal(ClipboardPasteKind.ExternalFiles, clipboard.GetPasteKind());
            Assert.Null(clipboard.GetClipboardEntry());
            Assert.True(clipboard.TryAdoptExternalFiles());
            Assert.Equal(new[] { first, second }, clipboard.GetClipboardEntry()!.SourcePaths);
            Assert.Equal(ClipboardOperation.Copy, clipboard.GetClipboardEntry()!.Operation);
            Assert.Null(clipboard.GetClipboardEntry()!.SourceTag);
            Assert.Equal(ClipboardPasteKind.InAppFiles, clipboard.GetPasteKind());
            await external.CopyTextAsync("replacement text");
            Assert.Equal(ClipboardPasteKind.None, clipboard.GetPasteKind());
            Assert.False(clipboard.HasClipboardFiles);
            Assert.False(clipboard.TryAdoptExternalFiles());
        }
        finally { root.Delete(true); }
    }
}
