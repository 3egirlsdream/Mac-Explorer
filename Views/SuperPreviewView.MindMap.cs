using Avalonia.Input;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private MindMapPreviewView? _mindMapPreview;

    private async Task<bool> TryShowMindMapAsync(string path, string extension, CancellationToken token, int version)
    {
        try
        {
            var result = await MindMapReader.ReadAsync(path, extension, token);
            if (token.IsCancellationRequested || version != _operationVersion || !IsVisible) return true;
            if (result.Status == MindMapReader.Status.NotMindMap) return false;

            if (result.Document is not { } document)
            {
                ShowPlaceholder(LocalizationText.Get(result.Status == MindMapReader.Status.TooLarge
                    ? "preview.mind-map.too-large" : "preview.mind-map.unavailable"));
                return true;
            }

            _mindMapPreview = new MindMapPreviewView();
            _mindMapPreview.SetDocument(document);
            PreviewHost.Children.Add(_mindMapPreview);
            PreviewPlaceholder.IsVisible = false;
            UpdatePreviewToolbar();
            AnimatePreviewContentTransition();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return true;
    }

    private void OnMindMapPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_mindMapPreview == null || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape && TryCloseMindMapSheetPicker())
            e.Handled = true;
        else if (e.Key == Key.Escape || (e.Key == Key.Space && !_mindMapPreview.IsSheetPickerOpen))
            OnSuperPreviewKeyDown(sender, e);
    }

    internal bool TryCloseMindMapSheetPicker() => _mindMapPreview?.TryCloseSheetPicker() == true;

    private void ClearMindMapPreview()
    {
        if (_mindMapPreview == null) return;
        _mindMapPreview.Clear();
        PreviewHost.Children.Remove(_mindMapPreview);
        _mindMapPreview = null;
    }
}
