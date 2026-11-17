using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using MacExplorer;
using MacExplorer.Controls;
using MacExplorer.Services.Impl;
using SkiaSharp;

namespace FolderIconDemo;

internal sealed class PlainFolderWindow : AppWindow
{
    private readonly LocalizationService _language = new(new DemoSettings(Program.Option("--language") ?? "zh-CN"));
    private readonly List<Bitmap> _owned = [];
    private readonly List<(Image Image, int Size)> _plain = [];
    private readonly Image _cover = new() { Width = 240, Height = 240 };
    private readonly Border _surface;
    private bool _dark = Program.Arguments.Contains("--dark");
    private bool _closed;

    public PlainFolderWindow()
    {
        Title = T("plain.title");
        Width = double.TryParse(Program.Option("--width"), out var width) ? width : 1060;
        Height = 800; MinWidth = 760; MinHeight = 680;
        ShowTitleBarDivider = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(42, 18, 42, 30) };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titles = new StackPanel { Spacing = 7 };
        titles.Children.Add(Label(T("plain.heading"), AppTypography.Display, FontWeight.SemiBold));
        titles.Children.Add(Label(T("plain.subtitle"), AppTypography.Body, muted: true));
        heading.Children.Add(titles);
        var theme = new AppButton { Content = T("theme"), Classes = { "secondary", "compact" }, VerticalAlignment = VerticalAlignment.Center };
        theme.Click += async (_, _) => { theme.IsEnabled = false; _dark = !_dark; await RefreshAsync(); theme.IsEnabled = true; };
        Grid.SetColumn(theme, 1); heading.Children.Add(theme); root.Children.Add(heading);

        var stage = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300"), VerticalAlignment = VerticalAlignment.Center };
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 0 };
        var heroImage = PlainImage(380);
        hero.Children.Add(heroImage);
        hero.Children.Add(Centered(T("plain.folder"), AppTypography.Title));
        stage.Children.Add(hero);
        var comparison = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        comparison.Children.Add(_cover);
        comparison.Children.Add(Centered(T("plain.photo"), AppTypography.Body, true));
        Grid.SetColumn(comparison, 1); stage.Children.Add(comparison);
        Grid.SetRow(stage, 1); root.Children.Add(stage);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 24 };
        var sizes = new StackPanel { Spacing = 10 };
        sizes.Children.Add(Label(T("plain.sizes"), AppTypography.Label, muted: true));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 17, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var size in new[] { 16, 24, 32, 48, 64, 96, 128 })
        {
            var item = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
            item.Children.Add(PlainImage(size));
            item.Children.Add(Centered(size.ToString(), AppTypography.Caption, true));
            row.Children.Add(item);
        }
        sizes.Children.Add(row); footer.Children.Add(sizes);
        var current = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
        current.Children.Add(new Image { Width = 96, Height = 96, Source = SvgIconCache.GetFolderIcon(192) });
        current.Children.Add(Centered(T("plain.current"), AppTypography.Caption, true));
        Grid.SetColumn(current, 1); footer.Children.Add(current);
        var note = Label(T("plain.note"), AppTypography.Caption, muted: true);
        note.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetColumnSpan(note, 2); Grid.SetRow(note, 1); footer.Children.Add(note);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        _surface = new Border { Child = root }; Content = _surface;
        SizeChanged += (_, _) => heroImage.Width = heroImage.Height = Math.Clamp(ClientSize.Width - 384, 280, 380);
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        Closed += (_, _) => { _closed = true; foreach (var bitmap in _owned) bitmap.Dispose(); _language.Dispose(); };
        Opened += async (_, _) =>
        {
            await RefreshAsync();
            if (_closed || Program.Option("--capture") is not { } path) return;
            await Task.Delay(100);
            using var capture = new RenderTargetBitmap(new PixelSize((int)ClientSize.Width * 2, (int)ClientSize.Height * 2), new Vector(192, 192));
            capture.Render(this); capture.Save(path);
            await File.WriteAllBytesAsync(Path.ChangeExtension(path, ".icon.png"), PlainFolderRenderer.RenderPng(1024, _dark));
            Console.WriteLine("CAPTURE " + path);
            if (Program.Arguments.Contains("--exit")) Close();
        };
    }

    private Image PlainImage(int size)
    {
        var image = new Image { Width = size, Height = size };
        _plain.Add((image, size)); return image;
    }

    private async Task RefreshAsync()
    {
        RequestedThemeVariant = _dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _surface.Background = new SolidColorBrush(Color.Parse(_dark ? "#24272E" : "#ECEDEF"));
        Resources["TitleBarBackgroundBrush"] = _surface.Background;
        var dark = _dark;
        var prepared = await Task.Run(() =>
        {
            var result = new List<(Image Target, byte[] Bytes)>();
            foreach (var (image, size) in _plain)
                result.Add((image, PlainFolderRenderer.RenderPng(Math.Min(1024, size * 2), dark)));
            using var first = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Assets", "lake.jpg"));
            using var second = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Assets", "coast.jpg"));
            result.Add((_cover, FrostedFolderRenderer.RenderPng(first, second, 480, dark: dark)));
            return result;
        });
        if (_closed) return;
        foreach (var (target, bytes) in prepared)
        {
            using var input = new MemoryStream(bytes);
            var previous = target.Source as Bitmap;
            var bitmap = new Bitmap(input); _owned.Add(bitmap); target.Source = bitmap;
            if (previous != null) { _owned.Remove(previous); previous.Dispose(); }
        }
    }

    private string T(string key) => _language.Get("demo.folder." + key);
    private TextBlock Centered(string text, string size, bool muted = false)
    {
        var label = Label(text, size, muted: muted);
        label.HorizontalAlignment = HorizontalAlignment.Center; return label;
    }
    private static TextBlock Label(string text, string size, FontWeight? weight = null, bool muted = false)
    {
        var label = AppTypography.BindFontSize(new TextBlock { Text = text, FontWeight = weight ?? FontWeight.Normal }, size);
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(muted ? "TextMutedBrush" : "TextPrimaryBrush"));
        return label;
    }
}
