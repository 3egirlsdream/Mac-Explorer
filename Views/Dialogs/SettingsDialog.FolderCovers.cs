using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private Task? _folderCoverPreviewsTask;
    private readonly List<Bitmap> _folderCoverPreviewImages = [];
    private static readonly string[] FolderStylePreviewNames =
        ["DefaultFolderIconPreview", "DefaultFolderCoverPreview", "FrostedFolderIconPreview", "FrostedFolderCoverPreview",
         "SkeuomorphicFolderIconPreview", "SkeuomorphicFolderCoverPreview"];
    internal Task FolderCoverPreviewsReady => _folderCoverPreviewsTask ?? Task.CompletedTask;

    private void InitializeFolderStylePicker()
        => FolderCoverStylePicker.AddHandler(InputElement.KeyDownEvent, OnFolderStyleKeyDown, RoutingStrategies.Tunnel);

    private void OnFolderStyleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        var picker = FolderCoverStylePicker;
        var index = e.Key switch
        {
            Key.Left or Key.Up => Math.Max(0, picker.SelectedIndex - 1),
            Key.Right or Key.Down => Math.Min(picker.ItemCount - 1, picker.SelectedIndex + 1),
            Key.Home => 0,
            Key.End => picker.ItemCount - 1,
            _ => -1
        };
        if (index < 0) return;
        e.Handled = true;
        picker.SelectedIndex = index;
        picker.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
    }

    private void OnFolderCoverStyleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || !ReferenceEquals(e.Source, FolderCoverStylePicker)) return;
        if (FolderCoverStylePicker.SelectedIndex is not (0 or 1 or 2)) return;
        var style = (FolderPhotoCoverStyle)FolderCoverStylePicker.SelectedIndex;
        _settingsService.Set(FolderPhotoCoverStyles.SettingKey, FolderPhotoCoverStyles.Value(style));
    }

    private void OnFolderCoverSettingChanged(string key)
    {
        if (key != FolderPhotoCoverStyles.SettingKey && key != ViewModels.FileListViewModel.FolderPhotoCoverSettingKey) return;
        void Sync()
        {
            if (_updateCancellation.IsCancellationRequested || !IsPageLoaded("display")) return;
            var initializing = _initializing; _initializing = true;
            try
            {
                FolderCoverStylePicker.SelectedIndex = (int)FolderPhotoCoverStyles.Read(_settingsService);
                FolderPhotoCoversToggle.IsChecked = _settingsService.Get(ViewModels.FileListViewModel.FolderPhotoCoverSettingKey, false);
            }
            finally { _initializing = initializing; }
        }
        if (Dispatcher.UIThread.CheckAccess()) Sync(); else Dispatcher.UIThread.Post(Sync);
    }

    private async Task LoadFolderCoverPreviewsAsync()
    {
        var token = _updateCancellation.Token;
        var loaded = new List<Bitmap>();
        try
        {
            await Task.Run(async () =>
            {
                foreach (var style in Enum.GetValues<FolderPhotoCoverStyle>())
                foreach (var photo in new[] { false, true })
                {
                    var bytes = await FolderCoverPreviewService.GetAsync(style, token, photo).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    using var input = new MemoryStream(bytes);
                    loaded.Add(new Bitmap(input));
                }
            }, token);
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < FolderStylePreviewNames.Length; i++)
                GetPageControl<Image>("display", FolderStylePreviewNames[i]).Source = loaded[i];
            _folderCoverPreviewImages.AddRange(loaded); loaded.Clear();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceError("Folder cover preview: {0}", ex); }
        finally { foreach (var image in loaded) image.Dispose(); }
    }

    private void DisposeFolderCoverPreviews()
    {
        if (IsPageLoaded("display"))
        {
            foreach (var name in FolderStylePreviewNames) GetPageControl<Image>("display", name).Source = null;
        }
        foreach (var image in _folderCoverPreviewImages) image.Dispose();
        _folderCoverPreviewImages.Clear();
    }
}
