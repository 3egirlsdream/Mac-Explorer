using MacExplorer.Controls;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Converters;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

internal sealed class FileConflictDialog : DialogWindow
{
    // Reference: 1462px panel / 76px action; shared actions have a fixed 32px height.
    internal const double DefaultWidth = 616;

    internal FileConflictDialog(FileConflict conflict)
    {
        Title = LocalizationText.Get("conflict.title");
        TitleBarContent = new TextBlock { Text = Title, Classes = { "dialog-window-title", "centered" } };
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/"))
        { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        Width = DefaultWidth;
        MinWidth = 400;
        MinHeight = 320;
        MaxHeight = 780;
        SizeToContent = SizeToContent.Height;
        CanResize = true;
        ToolTip.SetTip(TitleBarContent, Title);
        var body = new StackPanel { Spacing = 16 };
        var heading = Text("conflict.heading");
        AppTypography.BindFontSize(heading, AppTypography.Title);
        heading.FontWeight = FontWeight.SemiBold;
        var name = Value(conflict.Source.Name);
        AppTypography.BindFontSize(name, AppTypography.BodyLarge);
        var kind = Value(conflict.Source.KindText);
        kind.MaxWidth = 180;
        kind.HorizontalAlignment = HorizontalAlignment.Left;
        kind.Bind(TextBlock.ForegroundProperty, kind.GetResourceObservable("TextMutedBrush"));
        var identity = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 10 };
        identity.Children.Add(name);
        var separator = Divider(); separator.Width = 1; separator.Margin = new Thickness(0, 3);
        Grid.SetColumn(separator, 1); identity.Children.Add(separator);
        Grid.SetColumn(kind, 2); identity.Children.Add(kind);
        var summary = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(heading); summary.Children.Add(identity);
        var header = new Grid { ColumnDefinitions = new("52,*"), ColumnSpacing = 14, Margin = new Thickness(4) };
        header.Children.Add(new Image
        {
            Width = 52, Height = 52, Stretch = Stretch.Uniform,
            Source = new FileEntryToIconConverter().Convert(conflict.Source.DetailsIconSource, typeof(IImage), 128,
                LocalizationService.Current?.Culture ?? System.Globalization.CultureInfo.CurrentCulture) as IImage
        });
        Grid.SetColumn(summary, 1); header.Children.Add(summary);
        body.Children.Add(header);

        var comparison = new Grid { ColumnDefinitions = new("*,Auto,*"), ColumnSpacing = 20 };
        var source = Details("conflict.source", conflict.Source);
        var destination = Details("conflict.destination", conflict.Destination);
        var divider = Divider(); divider.Width = 1;
        comparison.Children.Add(source);
        Grid.SetColumn(divider, 1); comparison.Children.Add(divider);
        Grid.SetColumn(destination, 2); comparison.Children.Add(destination);
        body.Children.Add(new Border { Classes = { "settings-group" }, Padding = new Thickness(18, 12), Child = comparison });
        if (conflict.Source.IsDirectory != conflict.Destination.IsDirectory) body.Children.Add(Text("conflict.different-types"));
        if (conflict.IsRemote) body.Children.Add(Text("远程修改不可撤销。"));
        var all = new CheckBox { Content = Text("conflict.apply-all"), Margin = new Thickness(4, 0, 0, 0) };
        AutomationProperties.SetName(all, LocalizationText.Get("conflict.apply-all"));
        body.Children.Add(all);

        void Choose(FileConflictAction action) => Close(new FileConflictDecision(action, all.IsChecked == true));
        var cancel = new AppButton { Content = LocalizationText.Get("conflict.cancel"), Classes = { "ghost" } };
        AutomationProperties.SetName(cancel, LocalizationText.Get("conflict.cancel"));
        cancel.Click += (_, _) => Close();
        var skip = new AppButton { Content = LocalizationText.Get("conflict.Skip"), Classes = { "secondary" } };
        AutomationProperties.SetName(skip, LocalizationText.Get("conflict.Skip"));
        skip.Click += (_, _) => Choose(FileConflictAction.Skip);
        var keep = new AppButton { Content = LocalizationText.Get("conflict.KeepBoth"), Classes = { "primary" }, IsDefault = true };
        AutomationProperties.SetName(keep, LocalizationText.Get("conflict.KeepBoth"));
        keep.Click += (_, _) => Choose(FileConflictAction.KeepBoth);
        var older = new MenuItem
        {
            Header = LocalizationText.Get("conflict.ReplaceOlder"),
            IsEnabled = conflict.Source.IsDirectory == conflict.Destination.IsDirectory
        };
        older.Click += (_, _) => Choose(FileConflictAction.ReplaceOlder);
        var menu = new MenuFlyout(); menu.Items.Add(older);
        var replace = new AppSplitButton
        {
            Content = LocalizationText.Get(conflict.Source.IsDirectory && conflict.Destination.IsDirectory ? "conflict.merge" : "conflict.Replace"),
            Classes = { "secondary" }, Flyout = menu
        };
        AutomationProperties.SetName(replace, replace.Content.ToString());
        replace.Click += (_, _) => Choose(FileConflictAction.Replace);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new Control[] { skip, replace, keep })
        {
            button.MinWidth = button == skip ? 74 : button == keep ? 92 : 90;
            button.Margin = button == keep ? default : new Thickness(0, 0, 11, 0);
            buttons.Children.Add(button);
        }
        var footer = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), RowSpacing = 16 };
        var footerDivider = Divider(); footerDivider.Height = 1; Grid.SetColumnSpan(footerDivider, 3); footer.Children.Add(footerDivider);
        Grid.SetRow(cancel, 1); footer.Children.Add(cancel);
        Grid.SetRow(buttons, 1); Grid.SetColumn(buttons, 2); footer.Children.Add(buttons);
        var root = new Grid { Margin = new Thickness(16, 12, 16, 18), RowDefinitions = new("*,Auto"), RowSpacing = 6 };
        var scroll = new ScrollViewer { Content = body, MaxHeight = 620, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        root.Children.Add(scroll); Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        bool? wasNarrow = null;
        root.SizeChanged += (_, _) =>
        {
            kind.Measure(Size.Infinity);
            name.MaxWidth = Math.Max(30, root.Bounds.Width - 74 - kind.DesiredSize.Width - 21);
            var narrow = root.Bounds.Width < 570;
            if (wasNarrow == narrow) return;
            wasNarrow = narrow;
            comparison.ColumnDefinitions = new(narrow ? "*" : "*,Auto,*");
            comparison.RowDefinitions = new(narrow ? "Auto,Auto,Auto" : "Auto");
            Grid.SetColumn(divider, narrow ? 0 : 1); Grid.SetRow(divider, narrow ? 1 : 0);
            Grid.SetColumn(destination, narrow ? 0 : 2); Grid.SetRow(destination, narrow ? 2 : 0);
            divider.Width = narrow ? double.NaN : 1; divider.Height = narrow ? 1 : double.NaN;
            divider.Margin = narrow ? new Thickness(0, 14) : default;
            footer.ColumnDefinitions = new(narrow ? "*" : "Auto,*,Auto");
            footer.RowDefinitions = new(narrow ? "Auto,Auto,Auto" : "Auto,Auto");
            Grid.SetColumnSpan(footerDivider, narrow ? 1 : 3);
            Grid.SetRow(cancel, narrow ? 2 : 1);
            Grid.SetColumn(buttons, narrow ? 0 : 2);
            buttons.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        };
    }

    private static TextBlock Text(string key)
        => AppTypography.BindFontSize(new TextBlock { Text = LocalizationText.Get(key), TextWrapping = TextWrapping.Wrap }, AppTypography.Body);

    private static StackPanel Details(string titleKey, FileSystemEntry entry)
    {
        var panel = new StackPanel { Spacing = 8 };
        var title = Text(titleKey); title.FontWeight = FontWeight.SemiBold;
        AppTypography.BindFontSize(title, AppTypography.BodyLarge);
        title.Margin = new Thickness(0, 0, 0, 2); panel.Children.Add(title);
        var path = entry.FullPath.Contains("://", StringComparison.Ordinal)
            ? entry.FullPath[..Math.Max(entry.FullPath.IndexOf("://", StringComparison.Ordinal) + 3, entry.FullPath.LastIndexOf('/'))]
            : Path.GetDirectoryName(entry.FullPath) ?? entry.FullPath;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var displayPath = path == home ? "~" : path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
        foreach (var (key, value) in new[] { ("大小", entry.FormattedSize), ("修改时间", entry.ModifiedText), ("位置", displayPath) })
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 14 };
            var label = Text(key); label.MinWidth = 66;
            label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("TextMutedBrush"));
            var text = Value(value); ToolTip.SetTip(text, key == "位置" ? path : value);
            row.Children.Add(label); Grid.SetColumn(text, 1); row.Children.Add(text); panel.Children.Add(row);
        }
        return panel;
    }

    private static TextBlock Value(string value)
    {
        var text = new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        AppTypography.BindFontSize(text, AppTypography.Body);
        ToolTip.SetTip(text, value);
        return text;
    }

    private static Border Divider()
    {
        var divider = new Border();
        divider.Bind(Border.BackgroundProperty, divider.GetResourceObservable("DialogDividerBrush"));
        return divider;
    }
}

internal sealed class UiFileConflictResolver : IFileConflictResolver
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<FileConflictDecision> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var desktop = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                var owner = desktop?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive)
                    ?? desktop?.Windows.OfType<MainWindow>().FirstOrDefault();
                if (owner == null) throw new OperationCanceledException(cancellationToken);
                var dialog = new FileConflictDialog(conflict);
                using var cancel = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
                return await dialog.ShowDialog<FileConflictDecision?>(owner)
                    ?? throw new OperationCanceledException(cancellationToken);
            });
            return pending;
        }
        finally { _gate.Release(); }
    }
}
