using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Home")]
public sealed class HomeFileImageTests
{
    [AvaloniaFact(Explicit = true)]
    public void FolderStyleChangesRefreshAttachedHomeIconsAndPreserveTheDefaultCache()
    {
        var previous = SvgIconCache.FolderStyle;
        var settings = new ShortcutMemorySettings();
        using var styles = new FolderIconStyleService(settings);
        var entry = new FileSystemEntry { Name = "Album", FullPath = "/tmp/Album", IsDirectory = true };
        var image = new HomeFileImage(entry, 48, (_, _, _) => Task.FromResult<ThumbnailResult?>(null));
        var window = new Window { Content = image };
        try
        {
            window.Show();
            var original = image.Source;
            Assert.NotNull(original);
            settings.Set(FolderPhotoCoverStyles.SettingKey, "frosted");
            Assert.Equal(FolderPhotoCoverStyle.Frosted, SvgIconCache.FolderStyle);
            Assert.NotSame(original, image.Source);
            var frosted = image.Source;
            settings.Set(FolderPhotoCoverStyles.SettingKey, "skeuomorphic");
            Assert.Equal(FolderPhotoCoverStyle.Skeuomorphic, SvgIconCache.FolderStyle);
            Assert.NotSame(original, image.Source);
            Assert.NotSame(frosted, image.Source);
            Assert.Same(SvgIconCache.GetFolderIcon(FileThumbnailSizing.GetPixelSize(image.Width, window.RenderScaling),
                FolderPhotoCoverStyle.Skeuomorphic), image.Source);
            settings.Set(FolderPhotoCoverStyles.SettingKey, "default");
            Assert.Same(original, image.Source);
            window.Content = null;
            settings.Set(FolderPhotoCoverStyles.SettingKey, "frosted");
            Assert.Null(image.Source);
        }
        finally { window.Close(); SvgIconCache.SetFolderStyle(previous); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task RemovingImageCancelsRequestAndIgnoresLateResult()
    {
        var requested = new TaskCompletionSource<CancellationToken>();
        var finish = new TaskCompletionSource<ThumbnailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = new HomeFileImage(new FileSystemEntry { Name = "photo.png", FullPath = "/tmp/photo.png" }, 48,
            async (_, _, token) =>
            {
                requested.TrySetResult(token);
                try { return await finish.Task; }
                finally { providerReturned.TrySetResult(); }
            });
        var window = new Window { Width = 400, Height = 300, Content = image };
        window.Show();
        try
        {
            var token = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Content = null;
            Assert.True(token.IsCancellationRequested);
            finish.SetResult(new ThumbnailResult([1, 2, 3], ""));
            await providerReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(image.Source);
        }
        finally { window.Close(); finish.TrySetResult(null); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task FolderAndHiddenImageDoNotRequestThumbnails()
    {
        var calls = 0;
        var visibleRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var folder = new HomeFileImage(new FileSystemEntry { Name = "folder", FullPath = "/tmp/folder", IsDirectory = true }, 48,
            (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult<ThumbnailResult?>(null); });
        var hidden = new HomeFileImage(new FileSystemEntry { Name = "photo.png", FullPath = "/tmp/photo.png" }, 48,
            (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult<ThumbnailResult?>(null); }) { IsVisible = false };
        var visible = new HomeFileImage(new FileSystemEntry { Name = "visible.png", FullPath = "/tmp/visible.png" }, 48,
            (_, _, _) => { visibleRequested.TrySetResult(); return Task.FromResult<ThumbnailResult?>(null); });
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { folder, hidden, visible } } };
        window.Show();
        try
        {
            await visibleRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.NotNull(folder.Source);
        }
        finally { window.Close(); }
    }

}
