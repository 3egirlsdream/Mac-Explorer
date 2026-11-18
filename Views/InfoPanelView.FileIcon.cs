using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using MacExplorer.Models;

namespace MacExplorer.Views;

public partial class InfoPanelView
{
    private FileSystemEntry? _fileIconEntry;
    private CancellationTokenSource? _fileIconCts;

    private void ObserveFileIcon(FileSystemEntry entry)
    {
        if (!ReferenceEquals(_fileIconEntry, entry))
        {
            CancelFileIconLoad();
            _fileIconEntry = entry;
            entry.PropertyChanged += OnFileIconChanged;
        }
        StartFileIconLoad(entry, resolve: true);
    }

    private void CancelFileIconLoad()
    {
        if (_fileIconEntry != null) _fileIconEntry.PropertyChanged -= OnFileIconChanged;
        _fileIconEntry = null;
        _fileIconCts?.Cancel();
        _fileIconCts = null;
    }

    private void OnFileIconChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(FileSystemEntry.IconUrl) or nameof(FileSystemEntry.DetailsIconSource))) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (sender is FileSystemEntry entry && ReferenceEquals(_fileIconEntry, entry) && PreviewFileIcon.IsVisible)
                StartFileIconLoad(entry, resolve: false);
        });
    }

    private void StartFileIconLoad(FileSystemEntry entry, bool resolve)
    {
        _fileIconCts?.Cancel();
        var request = new CancellationTokenSource();
        _fileIconCts = request;
        _ = LoadFileIconAsync(entry, resolve, request);
    }

    private async Task LoadFileIconAsync(FileSystemEntry entry, bool resolve, CancellationTokenSource request)
    {
        try
        {
            if (resolve && entry.IconUrl == null && ViewModel is { } model)
                await model.ResolveFileIconsAsync([entry], request.Token);
            var source = entry.IconUrl;
            if (string.IsNullOrEmpty(source)) return;
            var bitmap = await FileListView.GetEntryBitmapAsync(source, request.Token);
            if (bitmap != null && !request.IsCancellationRequested && ReferenceEquals(_fileIconCts, request)
                && ReferenceEquals(_fileIconEntry, entry) && entry.IconUrl == source && PreviewFileIcon.IsVisible
                && ViewModel is { IsInfoPanelVisible: true, SelectedEntries.Count: 1 } viewModel
                && ReferenceEquals(viewModel.SelectedEntries[0], entry))
                PreviewFileIcon.Source = bitmap; // Borrow the shared cache bitmap; this panel does not dispose it.
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { Debug.WriteLine($"File icon preview: {ex}"); }
        finally
        {
            if (ReferenceEquals(_fileIconCts, request)) _fileIconCts = null;
            request.Dispose();
        }
    }
}
