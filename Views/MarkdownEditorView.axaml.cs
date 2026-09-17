using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using MacExplorer.Services.Markdown;

namespace MacExplorer.Views;

public partial class MarkdownEditorView : UserControl, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private MarkdownDocument? _document;
    private Task<bool>? _saveTask;
    private Task<bool>? _closeTask;
    private ContextMenu? _headingMenu;
    private TaskCompletionSource<CloseChoice>? _closeChoice;
    private bool _loading;
    private bool _saving;
    private bool _dirty;
    private bool _disposed;
    private string _mode = "Split";

    private enum CloseChoice { Cancel, Discard, Save }
    public event EventHandler? RequestClose;
    public string? FilePath => _document?.FilePath;

    public MarkdownEditorView()
    {
        _lifetimeToken = _lifetime.Token;
        InitializeComponent();
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.TextArea.IndentationStrategy = new MarkdownIndentationStrategy();
        Editor.TextChanged += OnTextChanged;
        Editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        Editor.ContextMenu = CreateEditorMenu();
        _refreshTimer.Tick += OnRefresh;
        KeyDown += OnEditorKeyDown;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.None; e.Handled = true; });
        AddHandler(DragDrop.DropEvent, (_, e) => e.Handled = true);
        if (Application.Current != null) Application.Current.ActualThemeVariantChanged += OnThemeChanged;
        ApplyHighlighting();
        SetMode("Split");
    }

    public async Task OpenAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loading = true;
        TitleText.Text = Path.GetFileName(path);
        PathText.Text = path;
        ErrorBanner.IsVisible = false;
        UpdateChrome();
        try
        {
            var document = await MarkdownDocument.OpenAsync(path, _lifetimeToken);
            if (_disposed) return;
            _document = document;
            Editor.Document = new TextDocument(document.Text);
            _dirty = false;
            Editor.IsReadOnly = false;
            Preview.SetDocument(document.Text, document.FilePath);
            Editor.Focus();
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) ShowError("打开失败：" + ex.Message);
            // A failed read never becomes an editable empty document.
        }
        finally
        {
            _loading = false;
            if (!_disposed) UpdateChrome();
        }
    }

    // Called from the main window's tunnel handler. Normal typing, Enter and Escape
    // continue to the editor/IME before the editor's bubble handler gets a chance to act.
    public void HandleWindowKeyDown(KeyEventArgs e)
    {
        if (_disposed || ConfirmOverlay.IsVisible) return;
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (!e.KeyModifiers.HasFlag(modifier)) return;
        switch (e.Key)
        {
            case Key.S:
                e.Handled = true;
                _ = SaveAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
            case Key.W:
                e.Handled = true;
                _ = TryCloseAsync();
                break;
            case Key.F:
                e.Handled = true;
                ShowSearch();
                break;
            case Key.G when SearchBar.IsVisible:
                e.Handled = true;
                Find(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
            case Key.Z when IsEditorInput(e.Source):
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Editor.Redo(); else Editor.Undo();
                break;
            case Key.Y when IsEditorInput(e.Source) && !OperatingSystem.IsMacOS():
                e.Handled = true;
                Editor.Redo();
                break;
            case Key.A when IsEditorInput(e.Source):
                e.Handled = true;
                Editor.SelectAll();
                break;
            case Key.C when IsEditorInput(e.Source):
                e.Handled = true;
                Editor.Copy();
                break;
            case Key.X when IsEditorInput(e.Source):
                e.Handled = true;
                Editor.Cut();
                break;
            case Key.V when IsEditorInput(e.Source):
                e.Handled = true;
                Editor.Paste();
                break;
            case Key.B when IsEditorInput(e.Source):
            case Key.I when IsEditorInput(e.Source):
            case Key.K when IsEditorInput(e.Source):
                e.Handled = true;
                ApplyFormat(e.Key == Key.B ? "Bold" : e.Key == Key.I ? "Italic" : "Link");
                break;
        }
    }

    private bool IsEditorInput(object? source)
    {
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, Editor)) return true;
        return false;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ConfirmOverlay.IsVisible) _closeChoice?.TrySetResult(CloseChoice.Cancel);
            else if (SearchBar.IsVisible) HideSearch();
            else _ = TryCloseAsync();
        }
        // Do not let unhandled shortcuts reach the file manager (including its demo shortcut).
        e.Handled = true;
    }

    public Task<bool> SaveAsync(bool saveAs = false)
    {
        if (_disposed || _document == null) return Task.FromResult(false);
        if (_saving) return _saveTask ?? Task.FromResult(false);
        _saving = true;
        _saveTask = SaveCoreAsync(saveAs);
        return _saveTask;
    }

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        var document = _document!;
        var text = Editor.Text;
        ErrorBanner.IsVisible = false;
        UpdateChrome();
        try
        {
            MarkdownDocument saved;
            if (saveAs)
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage?.CanSave != true) throw new IOException("当前环境不支持选择保存位置。");
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Markdown 另存为",
                    SuggestedFileName = Path.GetFileName(document.FilePath),
                    DefaultExtension = "md",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }]
                });
                if (file == null) return false;
                using (file)
                {
                    var path = file.TryGetLocalPath() ?? throw new IOException("请选择本地文件路径。");
                    saved = await document.SaveCopyAsync(path, text, _lifetimeToken);
                }
            }
            else saved = await document.SaveAsync(text, _lifetimeToken);
            if (_disposed) return true;
            _document = saved;
            // Edits made while disk I/O was in flight remain dirty; only the captured text was saved.
            _dirty = !string.Equals(Editor.Text, saved.Text, StringComparison.Ordinal);
            PathText.Text = saved.FilePath;
            RefreshPreview();
            return true;
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (!_disposed) ShowError("保存失败：" + ex.Message);
            return false;
        }
        finally
        {
            _saving = false;
            if (!_disposed) UpdateChrome();
        }
    }

    public Task<bool> TryCloseAsync()
    {
        if (_disposed) return Task.FromResult(true);
        if (_closeTask is { IsCompleted: false }) return _closeTask;
        return _closeTask = CloseCoreAsync();
    }

    private async Task<bool> CloseCoreAsync()
    {
        Editor.ContextMenu?.Close();
        _headingMenu?.Close();
        if (_saveTask is { IsCompleted: false } && !await _saveTask) return false;
        if (_disposed) return true;
        if (_document != null && !string.Equals(Editor.Text, _document.Text, StringComparison.Ordinal))
        {
            _closeChoice = new TaskCompletionSource<CloseChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorLayout.IsEnabled = false;
            ConfirmOverlay.IsVisible = true;
            CancelCloseButton.Focus();
            CloseChoice choice;
            try { choice = await _closeChoice.Task; }
            finally
            {
                _closeChoice = null;
                ConfirmOverlay.IsVisible = false;
                EditorLayout.IsEnabled = true;
            }
            if (_disposed) return true;
            if (choice == CloseChoice.Cancel) { Editor.Focus(); return false; }
            if (choice == CloseChoice.Save)
            {
                // Do not accept further input between "save and close" and its completion.
                EditorLayout.IsEnabled = false;
                try { if (!await SaveAsync()) return false; }
                finally { if (!_disposed) EditorLayout.IsEnabled = true; }
            }
        }
        Dispose();
        IsVisible = false;
        RequestClose?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.IsVisible = true;
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_disposed || _loading || _document == null) return;
        _dirty = true;
        UpdateChrome();
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnRefresh(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        if (_disposed || _document == null) return;
        RefreshPreview();
        UpdateChrome();
    }

    private void RefreshPreview()
    {
        if (_document == null || _disposed) return;
        var text = Editor.Text;
        _dirty = !string.Equals(text, _document.Text, StringComparison.Ordinal);
        if (_mode != "Source") Preview.SetDocument(text, _document.FilePath);
    }

    private void UpdateChrome()
    {
        SaveButton.IsEnabled = !_saving && _document != null;
        SaveAsButton.IsEnabled = !_saving && _document != null;
        FormatTools.IsEnabled = _document != null;
        if (_document != null)
        {
            TitleText.Text = Path.GetFileName(_document.FilePath) + (_dirty ? " •" : string.Empty);
            var lineEnding = _document.NewLine == "\r\n" ? "CRLF" : _document.NewLine == "\r" ? "CR" : "LF";
            StatusText.Text = $"{(_saving ? "正在保存…" : _dirty ? "未保存" : "已保存")}  ·  {_document.EncodingName}  ·  {lineEnding}  ·  {Editor.Document.LineCount:N0} 行";
        }
        else StatusText.Text = _loading ? "正在读取…" : "未打开可编辑的文档";
        OnCaretChanged(this, EventArgs.Empty);
    }

    private void OnCaretChanged(object? sender, EventArgs e) =>
        CaretText.Text = $"第 {Editor.TextArea.Caret.Line} 行，第 {Editor.TextArea.Caret.Column} 列";

    private void SetMode(string mode)
    {
        _mode = mode;
        SourcePane.IsVisible = mode != "Preview";
        PreviewPane.IsVisible = mode != "Source";
        Divider.IsVisible = mode == "Split";
        Grid.SetColumnSpan(SourcePane, mode == "Source" ? 3 : 1);
        Grid.SetColumn(PreviewPane, mode == "Preview" ? 0 : 2);
        Grid.SetColumnSpan(PreviewPane, mode == "Preview" ? 3 : 1);
        SourceMode.IsChecked = mode == "Source";
        SplitMode.IsChecked = mode == "Split";
        PreviewMode.IsChecked = mode == "Preview";
        RefreshPreview();
    }

    private void ApplyFormat(string action)
    {
        if (_document == null || _disposed || ConfirmOverlay.IsVisible) return;
        if (_mode == "Preview") SetMode("Split");
        MarkdownEditing.Apply(Editor, action, _document.NewLine);
    }

    private ContextMenu CreateEditorMenu()
    {
        var menu = new ContextMenu();
        void Item(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("撤销", () => Editor.Undo());
        Item("重做", () => Editor.Redo());
        menu.Items.Add(new Separator());
        Item("剪切", Editor.Cut);
        Item("复制", Editor.Copy);
        Item("粘贴", Editor.Paste);
        Item("全选", Editor.SelectAll);
        menu.Items.Add(new Separator());
        Item("行内代码", () => ApplyFormat("Code"));
        Item("删除线", () => ApplyFormat("Strike"));
        Item("分隔线", () => ApplyFormat("Rule"));
        return menu;
    }

    private void ShowSearch()
    {
        if (_document == null) return;
        if (_mode == "Preview") SetMode("Split");
        SearchBar.IsVisible = true;
        FindText.Focus();
        FindText.SelectAll();
    }

    private void HideSearch()
    {
        SearchBar.IsVisible = false;
        Editor.Focus();
    }

    private StringComparison SearchComparison => MatchCase.IsChecked == true
        ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void Find(bool backwards)
    {
        var query = FindText.Text ?? string.Empty;
        var text = Editor.Text;
        if (query.Length == 0) { SearchMessage.Text = "请输入查找内容"; return; }
        int index;
        if (backwards)
        {
            var start = Math.Min(Editor.SelectionStart - 1, text.Length - 1);
            index = start >= 0 ? text.LastIndexOf(query, start, SearchComparison) : -1;
            if (index < 0) index = text.LastIndexOf(query, SearchComparison);
        }
        else
        {
            index = text.IndexOf(query, Math.Min(Editor.SelectionStart + Editor.SelectionLength, text.Length), SearchComparison);
            if (index < 0) index = text.IndexOf(query, SearchComparison);
        }
        if (index < 0) { SearchMessage.Text = "未找到"; return; }
        Editor.CaretOffset = index + query.Length;
        Editor.SelectionStart = index;
        Editor.SelectionLength = query.Length;
        Editor.ScrollToLine(Editor.Document.GetLineByOffset(index).LineNumber);
        SearchMessage.Text = "已定位（到末尾后循环查找）";
    }

    private void OnReplace(object? sender, RoutedEventArgs e)
    {
        if (_document == null || Editor.IsReadOnly) return;
        var query = FindText.Text ?? string.Empty;
        if (query.Length == 0) return;
        if (string.Equals(Editor.SelectedText, query, SearchComparison))
            MarkdownEditing.ReplaceSelection(Editor, (ReplaceText.Text ?? string.Empty).ReplaceLineEndings(_document.NewLine));
        Find(false);
    }

    private void OnReplaceAll(object? sender, RoutedEventArgs e)
    {
        if (_document == null || Editor.IsReadOnly) return;
        var query = FindText.Text ?? string.Empty;
        if (query.Length == 0) return;
        var text = Editor.Text;
        var count = 0;
        for (var offset = 0; offset <= text.Length - query.Length;)
        {
            var match = text.IndexOf(query, offset, SearchComparison);
            if (match < 0) break;
            count++;
            offset = match + query.Length;
        }
        if (count > 0)
        {
            var replacement = (ReplaceText.Text ?? string.Empty).ReplaceLineEndings(_document.NewLine);
            Editor.Document.Replace(0, text.Length, text.Replace(query, replacement, SearchComparison));
        }
        SearchMessage.Text = $"已替换 {count} 处";
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyHighlighting();
    private void ApplyHighlighting() => Editor.SyntaxHighlighting =
        MarkdownHighlighting.Create(Application.Current?.ActualThemeVariant == ThemeVariant.Dark);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefresh;
        Editor.TextChanged -= OnTextChanged;
        Editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
        if (Application.Current != null) Application.Current.ActualThemeVariantChanged -= OnThemeChanged;
        _closeChoice?.TrySetResult(CloseChoice.Cancel);
        Preview.Clear();
        Editor.ContextMenu?.Close();
        _headingMenu?.Close();
        _headingMenu = null;
        Editor.Document = new TextDocument();
        _document = null;
    }

    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveAsync();
    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveAsync(true);
    private async void OnClose(object? sender, RoutedEventArgs e) => await TryCloseAsync();
    private void OnUndo(object? sender, RoutedEventArgs e) => Editor.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => Editor.Redo();
    private void OnFormat(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string action }) ApplyFormat(action); }
    private void OnMode(object? sender, RoutedEventArgs e) { if (sender is ToggleButton { Tag: string mode }) SetMode(mode); }
    private void OnFind(object? sender, RoutedEventArgs e) => ShowSearch();
    private void OnHideSearch(object? sender, RoutedEventArgs e) => HideSearch();
    private void OnFindNext(object? sender, RoutedEventArgs e) => Find(false);
    private void OnFindPrevious(object? sender, RoutedEventArgs e) => Find(true);
    private void OnCancelClose(object? sender, RoutedEventArgs e) => _closeChoice?.TrySetResult(CloseChoice.Cancel);
    private void OnDiscardClose(object? sender, RoutedEventArgs e) => _closeChoice?.TrySetResult(CloseChoice.Discard);
    private void OnSaveAndClose(object? sender, RoutedEventArgs e) => _closeChoice?.TrySetResult(CloseChoice.Save);
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Find(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    private void OnHeadingMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        _headingMenu?.Close();
        var menu = new ContextMenu();
        _headingMenu = menu;
        for (var level = 1; level <= 6; level++)
        {
            var action = $"H{level}";
            var item = new MenuItem { Header = $"{level} 级标题" };
            item.Click += (_, _) => ApplyFormat(action);
            menu.Items.Add(item);
        }
        menu.Open(button);
    }
}
