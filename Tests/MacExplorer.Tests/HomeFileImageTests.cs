using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeFileImageTests
{
    [AvaloniaFact]
    public async Task RemovingImageCancelsRequestAndIgnoresLateResult()
    {
        var requested = new TaskCompletionSource<CancellationToken>();
        var finish = new TaskCompletionSource<ThumbnailResult?>();
        var image = new HomeFileImage(new FileSystemEntry { Name = "photo.png", FullPath = "/tmp/photo.png" }, 48,
            (_, _, token) => { requested.TrySetResult(token); return finish.Task; });
        var window = new Window { Width = 400, Height = 300, Content = image };
        window.Show();
        try
        {
            var token = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Content = null;
            Assert.True(token.IsCancellationRequested);
            finish.SetResult(new ThumbnailResult([1, 2, 3], ""));
            await Task.Delay(100);
            Assert.Null(image.Source);
        }
        finally { window.Close(); finish.TrySetResult(null); }
    }

    [AvaloniaFact]
    public async Task FolderAndHiddenImageDoNotRequestThumbnails()
    {
        var calls = 0;
        var folder = new HomeFileImage(new FileSystemEntry { Name = "folder", FullPath = "/tmp/folder", IsDirectory = true }, 48,
            (_, _, _) => { calls++; return Task.FromResult<ThumbnailResult?>(null); });
        var hidden = new HomeFileImage(new FileSystemEntry { Name = "photo.png", FullPath = "/tmp/photo.png" }, 48,
            (_, _, _) => { calls++; return Task.FromResult<ThumbnailResult?>(null); }) { IsVisible = false };
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { folder, hidden } } };
        window.Show();
        try { await Task.Delay(200); Assert.Equal(0, calls); Assert.NotNull(folder.Source); }
        finally { window.Close(); }
    }
}
