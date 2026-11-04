using System.Diagnostics;
using Avalonia.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views;

public partial class InfoPanelView
{
    private readonly Func<string, CancellationToken, Task<DirectorySizeResult>> _directorySizeLoader;
    private CancellationTokenSource? _directorySizeCts;
    private string? _directorySizePath;
    private DirectorySizeResult? _directorySizeResult;
    private string? _directorySizeError;

    private void BeginDirectorySizeLoad(FileSystemEntry entry)
    {
        CancelDirectorySizeLoad();
        var request = new CancellationTokenSource();
        _directorySizeCts = request;
        _directorySizePath = entry.FullPath;
        RefreshDirectorySizeText(entry);
        _ = LoadDirectorySizeAsync(entry, request);
    }

    private void CancelDirectorySizeLoad()
    {
        var request = _directorySizeCts;
        _directorySizeCts = null;
        request?.Cancel(); // The request disposes its source after the worker stops.
        _directorySizePath = null;
        _directorySizeResult = null;
        _directorySizeError = null;
        ToolTip.SetTip(InfoSize, null);
    }

    private async Task LoadDirectorySizeAsync(FileSystemEntry entry, CancellationTokenSource request)
    {
        try
        {
            var result = await _directorySizeLoader(entry.FullPath, request.Token);
            if (!IsCurrentDirectorySizeRequest(entry, request)) return;
            _directorySizeResult = result;
            RefreshDirectorySizeText(entry);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Directory size: {ex}");
            if (!IsCurrentDirectorySizeRequest(entry, request)) return;
            _directorySizeError = ex.Message;
            RefreshDirectorySizeText(entry);
        }
        finally
        {
            if (ReferenceEquals(_directorySizeCts, request)) _directorySizeCts = null;
            request.Dispose();
        }
    }

    private bool IsCurrentDirectorySizeRequest(FileSystemEntry entry, CancellationTokenSource request)
        => ReferenceEquals(_directorySizeCts, request) && !request.IsCancellationRequested
           && ViewModel?.IsInfoPanelVisible == true && ViewModel.SelectedEntries.Count == 1
           && ReferenceEquals(ViewModel.SelectedEntries[0], entry);

    private void RefreshDirectorySizeText(FileSystemEntry entry)
    {
        if (_directorySizePath != entry.FullPath) return;
        var text = _directorySizeResult is { } result
            ? result.IsComplete ? FileSystemEntry.FormatSize(result.Bytes)
                : LocalizationText.Get("info.size.incomplete", FileSystemEntry.FormatSize(result.Bytes))
            : LocalizationText.Get(_directorySizeError == null ? "计算中…" : "计算失败");
        InfoSize.Text = text;
        SelectedFileSummary.Text = $"{entry.KindText} · {text}";
        ToolTip.SetTip(InfoSize, _directorySizeError ?? (_directorySizeResult is { IsComplete: false }
            ? LocalizationText.Get("info.size.incomplete-tip") : null));
    }
}
