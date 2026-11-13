using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;

namespace MacExplorer.Views.Dialogs;

internal sealed class GitDiffDialog : DialogWindow
{
    private readonly DiffPreviewView _preview = new();
    internal GitDiffDialog(string text, bool staged)
    {
        Title = LocalizationText.Get(staged ? "git.staged" : "git.unstaged");
        TitleBarContent = new TextBlock { Text = Title, Classes = { "dialog-window-title" } };
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/"))
        { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        Width = 1000; MinWidth = 360; MaxHeight = 780;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        CanResize = true; Height = 660; MinHeight = 360; SizeToContent = SizeToContent.Manual;
        var body = new Grid { Margin = new Thickness(16), RowDefinitions = new("Auto,*"), RowSpacing = 10 };
        if (string.IsNullOrEmpty(text)) body.Children.Add(Message("git.no-diff"));
        else
        {
            if (text.Contains("Binary files ", StringComparison.Ordinal) || text.Contains("GIT binary patch", StringComparison.Ordinal))
                body.Children.Add(Message("git.binary-diff"));
            else body.RowSpacing = 0;
            _preview.SetDocument(DiffPreviewDocument.Parse(text)); Grid.SetRow(_preview, 1); body.Children.Add(_preview);
        }
        Content = body;
        Closed += (_, _) => _preview.Clear();
    }

    private static TextBlock Message(string key)
        => AppTypography.BindFontSize(new TextBlock { Text = LocalizationText.Get(key), TextWrapping = TextWrapping.Wrap }, AppTypography.Body);
}
