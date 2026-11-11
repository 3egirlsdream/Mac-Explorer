using Avalonia.Input;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private DiffPreviewView? _diffPreview;

    private async Task ShowDiffPreviewAsync(string path, CancellationToken token, int version)
    {
        try
        {
            var document = await DiffPreviewDocument.ReadAsync(path, token);
            if (token.IsCancellationRequested || version != _operationVersion || !IsVisible) return;
            _diffPreview = new DiffPreviewView();
            _diffPreview.SetDocument(document);
            PreviewHost.Children.Add(_diffPreview);
            PreviewTextScroll.IsVisible = false;
            PreviewText.IsVisible = false;
            PreviewImage.IsVisible = false;
            PreviewPlaceholder.IsVisible = false;
            FolderSummary.IsVisible = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (token.IsCancellationRequested || version != _operationVersion || !IsVisible) return;
            ShowPlaceholder(LocalizationText.Get(ex is TextPreviewTooLargeException
                ? "preview.text-too-large" : "preview.text-unreadable"));
        }
    }

    private void OnDiffPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_diffPreview != null && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Space or Key.Escape)
            OnSuperPreviewKeyDown(sender, e);
    }

    private void ClearDiffPreview()
    {
        if (_diffPreview == null) return;
        _diffPreview.Clear();
        PreviewHost.Children.Remove(_diffPreview);
        _diffPreview = null;
    }
}
