using MacExplorer.Models;
using MacExplorer.Services.Markdown;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private MarkdownPreviewView? _markdownPreview;

    private async Task ShowMarkdownPreviewAsync(string path, FileSystemEntry entry, CancellationToken token, int version)
    {
        try
        {
            var text = await MarkdownDocument.ReadPreviewAsync(path, token);
            if (token.IsCancellationRequested || version != _operationVersion || !IsVisible) return;
            _markdownPreview ??= new MarkdownPreviewView();
            if (_markdownPreview.Parent == null) PreviewHost.Children.Add(_markdownPreview);
            _markdownPreview.SetDocument(text, path);
            _markdownPreview.IsVisible = true;
            PreviewTextScroll.IsVisible = false;
            PreviewText.IsVisible = false;
            PreviewImage.IsVisible = false;
            PreviewPlaceholder.IsVisible = false;
            FolderSummary.IsVisible = false;
            PreviewMetaText.Text = $"Markdown · {entry.FormattedSize}";
            QuickLookButton.IsVisible = _quickLookService != null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == _operationVersion && IsVisible && !token.IsCancellationRequested)
                ShowPlaceholder("Markdown 预览失败：" + ex.Message);
        }
    }

    private void ClearMarkdownPreview()
    {
        if (_markdownPreview == null) return;
        _markdownPreview.Clear();
        PreviewHost.Children.Remove(_markdownPreview);
        _markdownPreview = null;
    }
}
