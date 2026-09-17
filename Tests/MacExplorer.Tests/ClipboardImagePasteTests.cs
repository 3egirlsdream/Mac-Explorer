using System.Runtime.Versioning;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[SupportedOSPlatform("macos")]
public sealed class ClipboardImagePasteTests : IDisposable
{
    private const string TimestampImageNamePattern = @"^图片 \d{4}-\d{2}-\d{2} \d{2}\.\d{2}\.\d{2}\.png$";

    private readonly string _root = Path.Combine("/tmp", "fkfinder-image-paste-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MacFileService _files = new();

    private string DirectoryAt(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task PasteImageWritesClipboardBytesUnderTimestampName()
    {
        var target = DirectoryAt("target");
        var image = new ClipboardImageData([1, 2, 3, 4, 5], ".png");
        var viewModel = new FileOpsViewModel(new ImageClipboardService(image), _files);
        var status = new List<string>();

        await viewModel.PasteImageAsync(target, image, status.Add);

        var created = Assert.Single(Directory.GetFiles(target));
        Assert.Matches(TimestampImageNamePattern, Path.GetFileName(created));
        Assert.Equal(image.Bytes, await File.ReadAllBytesAsync(created));
        Assert.Contains(status, message => message.Contains(Path.GetFileName(created)));
    }

    [Fact]
    public async Task PasteImageAppendsSuffixWhenTimestampNameAlreadyExists()
    {
        var target = DirectoryAt("target");
        var image = new ClipboardImageData([9, 9], ".png");
        var viewModel = new FileOpsViewModel(new ImageClipboardService(image), _files);

        // 预置当前及随后两秒的候选名，使本次粘贴必然落入重名分支
        var stamp = DateTime.Now;
        for (var offset = 0; offset < 3; offset++)
            await File.WriteAllBytesAsync(Path.Combine(target, $"图片 {stamp.AddSeconds(offset):yyyy-MM-dd HH.mm.ss}.png"), [7]);

        await viewModel.PasteImageAsync(target, image);

        var names = Directory.GetFiles(target).Select(Path.GetFileName).ToArray();
        Assert.Equal(4, names.Length);
        Assert.Contains(names, name => name!.EndsWith(" 2.png"));
    }
}

internal sealed class ImageClipboardService(ClipboardImageData? image = null) : IClipboardService
{
    private ClipboardEntry? _entry;

    public ClipboardImageData? Image { get; set; } = image;
    public ClipboardPasteKind? KindOverride { get; set; }

    public bool HasClipboardFiles => _entry is { IsEmpty: false };

    public bool HasPasteableContent => GetPasteKind() != ClipboardPasteKind.None;

    public ClipboardPasteKind GetPasteKind() => KindOverride
        ?? (HasClipboardFiles ? ClipboardPasteKind.InAppFiles : Image != null ? ClipboardPasteKind.Image : ClipboardPasteKind.None);

    public bool TryAdoptExternalFiles() => false;
    public ClipboardImageData? ReadExternalImage() => Image;

    public void CopyFiles(string[] paths)
        => _entry = new ClipboardEntry { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Copy };

    public void CutFiles(string[] paths)
        => _entry = new ClipboardEntry { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Cut };

    public Task CopyTextAsync(string text) => Task.CompletedTask;
    public Task PasteFilesAsync(string targetDirectory) => Task.CompletedTask;
    public ClipboardEntry? GetClipboardEntry() => _entry;
    public void Clear() => _entry = null;
}
