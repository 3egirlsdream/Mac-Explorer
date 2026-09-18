using Avalonia.Controls;
using Avalonia.Layout;
using LiquidGlassAvaloniaUI;
using MacExplorer.Services.Markdown;

namespace MacExplorer.Views;

public partial class MainWindow
{
    private MarkdownEditorView? _markdownEditor;
    private long _markdownOpenGeneration;
    private bool IsMarkdownEditorOpen => _markdownEditor?.IsVisible == true;

    public async Task OpenMarkdownEditorAsync(string path)
    {
        if (_shutdownStarted || IsModalInteractionBlocked || !MarkdownDocument.IsMarkdown(path)) return;
        var generation = ++_markdownOpenGeneration;
        if (IsMarkdownEditorOpen)
        {
            if (string.Equals(_markdownEditor!.FilePath, path, StringComparison.Ordinal)) return;
            if (!await _markdownEditor.TryCloseAsync()) return;
        }
        // Multiple requests can await the same unsaved-changes dialog. Only
        // the newest may attach a replacement; otherwise an untracked editor
        // remains in the overlay host with live theme/document subscriptions.
        if (_shutdownStarted || generation != _markdownOpenGeneration) return;

        ActiveWorkspace?.CloseTransientUi(null);
        PaneLayoutPopup.IsOpen = false;
        CloseGlobalSearch();
        SuperPreviewControl.Close();

        // Use the exact same overlay host as SuperPreview, below the app's modal DialogHost.
        // Construct lazily: starting the file manager should not construct an editor or Markdown renderer.
        var host = SuperPreviewControl.Parent as Panel
            ?? throw new InvalidOperationException("超级预览缺少窗内浮层容器。");
        var editor = new MarkdownEditorView
        {
            IsVisible = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        LiquidGlassBackdrop.SetIsExcludedFromCapture(editor, true);
        _markdownEditor = editor;
        editor.RequestClose += OnMarkdownEditorClosed;
        host.Children.Insert(host.Children.IndexOf(SuperPreviewControl) + 1, editor);
        editor.Focus();
        try
        {
            // Native live-preview surfaces must not float above the Markdown editor.
            await _livePreviewCoordinator.ActivateAsync(null);
            if (ReferenceEquals(_markdownEditor, editor) && editor.IsVisible)
                await editor.OpenAsync(path);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_markdownEditor, editor)) editor.ShowError("无法打开编辑器：" + ex.Message);
        }
    }

    private void OnMarkdownEditorClosed(object? sender, EventArgs e)
    {
        if (sender is not MarkdownEditorView editor || !ReferenceEquals(editor, _markdownEditor)) return;
        editor.RequestClose -= OnMarkdownEditorClosed;
        if (editor.Parent is Panel panel) panel.Children.Remove(editor);
        _markdownEditor = null;
        if (_shutdownStarted) return;
        Focus();
        _ = RestoreMarkdownWorkspaceAsync();
    }

    private async Task RestoreMarkdownWorkspaceAsync()
    {
        try
        {
            if (!_shutdownStarted && !IsMarkdownEditorOpen) await ActivateSelectedWorkspaceAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Restoring Markdown workspace failed: {ex}"); }
    }

    private async Task<bool> CloseMarkdownEditorForShutdownAsync()
    {
        try { return _markdownEditor == null || await _markdownEditor.TryCloseAsync(); }
        catch (Exception ex)
        {
            _markdownEditor?.ShowError("无法关闭编辑器，修改仍已保留：" + ex.Message);
            return false;
        }
    }

    private void DisposeMarkdownEditor()
    {
        ++_markdownOpenGeneration;
        if (_markdownEditor is not { } editor) return;
        editor.RequestClose -= OnMarkdownEditorClosed;
        editor.Dispose();
        if (editor.Parent is Panel panel) panel.Children.Remove(editor);
        _markdownEditor = null;
    }
}
