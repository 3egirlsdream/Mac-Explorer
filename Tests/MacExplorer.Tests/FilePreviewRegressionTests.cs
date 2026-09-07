using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    private static readonly byte[] PreviewTestPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [AvaloniaFact]
    public async Task InfoPanelAppliesBackgroundTextAndImageResultsAndUsesIconForUnsupportedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fkfinder-preview-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var vm = CreateViewModel(new FakeFileService(directory));
        var panel = new InfoPanelView { DataContext = vm };
        var window = new Window { Width = 400, Height = 600, Content = panel };
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "a.txt"), "preview loaded from worker");
            await File.WriteAllBytesAsync(Path.Combine(directory, "b.png"), PreviewTestPng);
            await File.WriteAllBytesAsync(Path.Combine(directory, "c.unknown"), [0, 1, 2, 3]);
            window.Show();
            vm.IsInfoPanelVisible = true;
            await panel.SetLivePreviewStateAsync(true, 1);

            Select("a.txt");
            await WaitForPreviewAsync(() => panel.FindControl<TextBox>("PreviewText")!.IsVisible);
            Assert.Equal("preview loaded from worker", panel.FindControl<TextBox>("PreviewText")!.Text);

            Select("b.png");
            await WaitForPreviewAsync(() => panel.FindControl<Image>("PreviewImage")!.IsVisible);
            Assert.NotNull(panel.FindControl<Image>("PreviewImage")!.Source);

            Select("c.unknown");
            await WaitForPreviewAsync(() => panel.FindControl<Image>("PreviewFileIcon")!.IsVisible);
            Assert.NotNull(panel.FindControl<Image>("PreviewFileIcon")!.Source);
            Assert.False(panel.FindControl<Image>("PreviewImage")!.IsVisible);

            Select("missing.txt");
            await panel.ReloadLivePreviewAsync(1);
            await WaitForPreviewAsync(() => panel.FindControl<Image>("PreviewFileIcon")!.IsVisible);
            Assert.NotNull(panel.FindControl<Image>("PreviewFileIcon")!.Source);

            Select("folder", isDirectory: true);
            await panel.ReloadLivePreviewAsync(1);
            Assert.True(panel.FindControl<Image>("PreviewFileIcon")!.IsVisible);
            Assert.NotNull(panel.FindControl<Image>("PreviewFileIcon")!.Source);

            // A cancelled older selection must not replace the latest one.
            Select("a.txt");
            await panel.ReloadLivePreviewAsync(1);
            Select("b.png");
            await panel.ReloadLivePreviewAsync(1);
            await WaitForPreviewAsync(() => panel.FindControl<Image>("PreviewImage")!.IsVisible);
            Assert.False(panel.FindControl<TextBox>("PreviewText")!.IsVisible);
        }
        finally
        {
            await panel.SetLivePreviewStateAsync(false, 2);
            window.Close();
            Directory.Delete(directory, true);
        }

        void Select(string name, bool isDirectory = false)
        {
            vm.SelectedEntries.Clear();
            vm.SelectedEntries.Add(new FileSystemEntry
            {
                FullPath = Path.Combine(directory, name), Name = name,
                Extension = Path.GetExtension(name), IsDirectory = isDirectory,
                IconKey = name.EndsWith(".png", StringComparison.Ordinal) ? "file-image" : "file-generic"
            });
        }
    }

    [AvaloniaTheory]
    [InlineData("ListEntryTemplate", ViewMode.List)]
    [InlineData("GridEntryTemplate", ViewMode.Grid)]
    public async Task RealizedFileIconsLoadThumbnailsOffThreadAndKeepThemAfterPresentationChanges(string templateName, ViewMode mode)
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "fkfinder-thumb-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(cachePath, PreviewTestPng);
        var service = new PreviewThumbnailStub(cachePath);
        using var vm = CreateViewModel(new FakeFileService("/tmp"),
            sortFilter: new SortFilterViewModel { ViewMode = mode }, thumbnailService: service);
        var view = new FileListView { DataContext = vm };
        var entry = new FileSystemEntry { FullPath = "/tmp/visible.png", Name = "visible.png", Extension = ".png" };
        if (mode == ViewMode.Grid)
        {
            // Simulate a small thumbnail retained from the details list.
            entry.ThumbnailUrl = cachePath;
            entry.GeneratedThumbnailPixelSize = 32;
        }
        var template = Assert.IsAssignableFrom<IDataTemplate>(view.Resources[templateName]);
        var card = Assert.IsAssignableFrom<Control>(template.Build(entry));
        card.DataContext = entry;
        var window = new Window { Width = 600, Height = 200, Content = card };
        try
        {
            window.Show();
            await WaitForPreviewAsync(() => entry.ThumbnailUrl == cachePath
                && FileListView.TryGetCachedEntryImage(cachePath) != null);
            var image = card.GetVisualDescendants().OfType<Image>().Single(i => i.Classes.Contains("entry-icon-image"));
            await WaitForPreviewAsync(() => ReferenceEquals(image.Source, FileListView.TryGetCachedEntryImage(cachePath)));
            Assert.False(service.CalledOnUiThread);
            Assert.InRange(service.PixelSize, 32, 256);
            Assert.Equal(service.PixelSize, entry.GeneratedThumbnailPixelSize);
            entry.IsSelected = true;
            entry.GitStatus = GitFileStatus.Modified;
            entry.RaiseIconBindingChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(FileListView.TryGetCachedEntryImage(cachePath), image.Source);
            Assert.Equal(1, service.CallCount);
        }
        finally
        {
            window.Close();
            File.Delete(cachePath);
        }
    }

    [AvaloniaFact]
    public async Task RemovingARealizedRowCancelsItsThumbnailRequest()
    {
        var service = new PreviewThumbnailStub(null);
        using var vm = CreateViewModel(new FakeFileService("/tmp"), thumbnailService: service);
        var view = new FileListView { DataContext = vm };
        var entry = new FileSystemEntry { FullPath = "/tmp/slow.png", Name = "slow.png", Extension = ".png" };
        var template = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["ListEntryTemplate"]);
        var card = Assert.IsAssignableFrom<Control>(template.Build(entry));
        card.DataContext = entry;
        var window = new Window { Width = 600, Height = 200, Content = card };
        try
        {
            window.Show();
            await WaitForPreviewAsync(() => service.CallCount > 0);
            window.Content = null;
            await WaitForPreviewAsync(() => service.WasCancelled);
            Assert.Null(entry.ThumbnailUrl);
        }
        finally { window.Close(); }
    }

    private static async Task WaitForPreviewAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(ready(), "Preview did not reach the expected state.");
    }

    private sealed class PreviewThumbnailStub(string? cachePath) : IThumbnailService
    {
        public int CallCount;
        public bool CalledOnUiThread;
        public int PixelSize;
        public bool WasCancelled;
        public async Task<ThumbnailResult?> GetThumbnailResultAsync(string path, int size, CancellationToken ct = default)
        {
            CalledOnUiThread = Dispatcher.UIThread.CheckAccess();
            PixelSize = size;
            Interlocked.Increment(ref CallCount);
            if (cachePath != null) return new ThumbnailResult(PreviewTestPng, cachePath);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { WasCancelled = true; throw; }
            return null;
        }
        public async Task<byte[]?> GetThumbnailAsync(string path, int size, CancellationToken ct = default)
            => (await GetThumbnailResultAsync(path, size, ct))?.Bytes;
        public Task<byte[]?> GetFaceCropAsync(string path, float bx, float by, float bw, float bh, int maxPixelSize = 128, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);
        public bool IsImageFile(string extension) => extension == ".png";
        public void EvictFromCache(string path) { }
    }
}
