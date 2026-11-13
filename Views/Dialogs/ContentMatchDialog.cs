using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;

namespace MacExplorer.Views.Dialogs;

internal sealed class ContentMatchDialog : DialogWindow
{
    private readonly FileSystemEntry _entry;
    private readonly string _keyword;
    private readonly DocumentMatch? _initialMatch;
    private readonly Panel _preview = new();
    private DocumentPreviewHost? _native;
    private TextEditor? _text;
    private bool _excerptOnly;
    private readonly CancellationTokenSource _lifetime = new();
    internal ContentMatchDialog(FileSystemEntry entry, string keyword, DocumentMatch? initialMatch = null)
    {
        Title = LocalizationText.Get("content.matches");
        TitleBarContent = new TextBlock { Text = Title, Classes = { "dialog-window-title" } };
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/"))
        { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        Width = 1040; MinWidth = 360; Height = 626; MaxHeight = 780;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _entry = entry; _keyword = keyword; _initialMatch = initialMatch; CanResize = true; MinHeight = 400;
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/"))
            { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        _preview.Margin = new Thickness(16);
        Content = _preview;
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _native?.Dispose(); _text?.Document?.Text = ""; _lifetime.Dispose(); };
    }
    private async Task LoadAsync()
    {
        try
        {
            var extension = Path.GetExtension(_entry.FullPath).ToLowerInvariant();
            if (extension is ".txt" or ".md" or ".markdown")
            {
                string text;
                try { text = await TextPreviewReader.ReadAsync(_entry.FullPath, _lifetime.Token); }
                catch (TextPreviewTooLargeException)
                {
                    _excerptOnly = true;
                    text = (_initialMatch ?? _entry.SearchMatches.FirstOrDefault())?.Snippet ?? "";
                }
                if (_lifetime.IsCancellationRequested) return;
                _text = new TextEditor { IsReadOnly = true, ShowLineNumbers = !_excerptOnly, WordWrap = _excerptOnly,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8), Document = new TextDocument(text) };
                _text.Bind(TextEditor.FontFamilyProperty, _text.GetResourceObservable("FontFamilyMono"));
                _text.Bind(TextEditor.ForegroundProperty, _text.GetResourceObservable("TextPrimaryBrush"));
                Avalonia.Automation.AutomationProperties.SetName(_text, MacExplorer.Services.Impl.LocalizationText.Get("content.matches"));
                AppTypography.BindFontSize(_text, AppTypography.Body); _preview.Children.Add(_text);
            }
            else
            {
                _native = new DocumentPreviewHost(_ => { });
                if (_native.Load(_entry.FullPath, extension == ".pdf", false) != 1) throw new IOException(MacExplorer.Services.Impl.LocalizationText.Get("preview.text-unreadable"));
                _preview.Children.Add(_native);
            }
            if ((_initialMatch ?? _entry.SearchMatches.FirstOrDefault()) is { } first) Navigate(first);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) _preview.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }); }
    }
    private void Navigate(DocumentMatch match)
    {
        ToolTip.SetTip(_preview, match.Location.DisplayName + (_excerptOnly
            ? " · " + MacExplorer.Services.Impl.LocalizationText.Get("content.excerpt-only") : ""));
        if (match.Location.Kind == "page") _native?.NavigateTo(match.Location.Number, _keyword);
        if (_text?.Document is not { } document || match.Location.Kind != "line") return;
        if (_excerptOnly)
        {
            document.Text = match.Snippet;
            var start = document.Text.IndexOf(_keyword, StringComparison.OrdinalIgnoreCase);
            _text.Select(Math.Max(0, start), start < 0 ? 0 : _keyword.Length);
            return;
        }
        var line = document.GetLineByNumber(Math.Clamp(match.Location.Number, 1, document.LineCount));
        var text = document.GetText(line.Offset, line.Length); var at = text.IndexOf(_keyword, StringComparison.OrdinalIgnoreCase);
        _text.Select(line.Offset + Math.Max(0, at), at < 0 ? line.Length : Math.Min(_keyword.Length, line.Length - at));
        _text.ScrollToLine(line.LineNumber);
    }
}
