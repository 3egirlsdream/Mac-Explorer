using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MacExplorer;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Models;
using SkiaSharp;

namespace FolderIconDemo;

internal static class Program
{
    public static string[] Arguments = [];
    public static string? Option(string key)
    {
        var index = Array.IndexOf(Arguments, key);
        return index >= 0 && index + 1 < Arguments.Length ? Arguments[index + 1] : null;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = args;
        if (args.Contains("--render-paper-review") || args.Contains("--render-glass-review"))
        {
            SkeuomorphicReview.Export(Option("--reference") ?? throw new ArgumentException("--reference is required"),
                Option("--output") ?? throw new ArgumentException("--output is required"), Option("--default-folder"),
                args.Contains("--render-glass-review"));
            return;
        }
        AppBuilder.Configure<DemoApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class DemoApp : Application
{
    public override void Initialize()
    {
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://MacExplorer/"))
            { Source = new Uri("avares://MacExplorer/Assets/TypographyTokens.axaml") });
        Styles.Add(new FluentTheme());
        foreach (var asset in new[] { "Assets/ThemeTokens.axaml", "Assets/Styles.axaml", "Controls/AppWindow.axaml",
                     "Assets/ComponentStyles.axaml" })
            Styles.Add(new StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri("avares://MacExplorer/" + asset) });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = Program.Arguments.Contains("--skeuomorphic") ? new SkeuomorphicFolderWindow() : Program.Arguments.Contains("--plain") ? new PlainFolderWindow() : new DemoWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class DemoWindow : AppWindow
{
    private readonly LocalizationService _language = new(new DemoSettings(Program.Option("--language") ?? "zh-CN"));
    private readonly List<byte[]> _photos = [];
    private readonly int[] _photoVersions = new int[4];
    private readonly HomeThumbnailCache _cache = new();
    private readonly FolderCoverRenderService _renderer = new();
    private readonly Dictionary<Image, HomeThumbnailCache.Lease> _leases = [];
    private CancellationTokenSource? _refreshCancellation;
    private Task _imageUpdates = Task.CompletedTask;
    private int _generation;
    private bool _closed;
    private readonly Image _hero = new() { Width = 440, Height = 440 };
    private readonly List<Image> _examples = [];
    private readonly List<Image> _sizes = [];
    private readonly TextBlock _name;
    private readonly TextBlock _error;
    private readonly Border _surface;
    private readonly Grid _showcase;
    private readonly StackPanel _heroGroup;
    private readonly Slider _frost = new() { Minimum = 10, Maximum = 30, Value = FrostedFolderRenderer.DefaultFrost * 100, Width = 145 };
    private readonly Slider _blur = new() { Minimum = 2, Maximum = 18, Value = 10, Width = 145 };
    private readonly TextBlock _frostValue;
    private readonly TextBlock _blurValue;
    private readonly AppToggleButton _imageCount;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly string[] _names = ["travel", "coast", "architecture", "forest"];
    private bool _dark = Program.Arguments.Contains("--dark");
    private bool _two = !Program.Arguments.Contains("--single");
    private int _selected;

    public DemoWindow()
    {
        Title = T("title");
        Width = double.TryParse(Program.Option("--width"), out var width) ? width : 1180;
        Height = 850; MinWidth = 660; MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowTitleBarDivider = false;
        foreach (var file in new[] { "lake", "coast", "architecture", "forest" })
            _photos.Add(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", file + ".jpg")));
        if (!Program.Arguments.Contains("--original-cover"))
        {
            _photos[0] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "reference-cover.png"));
        }

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(30, 18, 30, 20) };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(12, 0, 12, 8) };
        var titles = new StackPanel { Spacing = 5 };
        titles.Children.Add(Label(T("heading"), AppTypography.Display, FontWeight.SemiBold));
        titles.Children.Add(Label(T("subtitle"), AppTypography.Body, muted: true));
        heading.Children.Add(titles);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Action(T("theme"), () => { _dark = !_dark; Refresh(); }));
        _imageCount = new AppToggleButton { Content = T("two"), IsChecked = _two, Classes = { "secondary", "compact" }, Margin = new Thickness(6, 0, 0, 0) };
        _imageCount.IsCheckedChanged += (_, _) => { _two = _imageCount.IsChecked == true; Refresh(); };
        actions.Children.Add(_imageCount);
        Grid.SetColumn(actions, 1); heading.Children.Add(actions);
        root.Children.Add(heading);

        _showcase = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), VerticalAlignment = VerticalAlignment.Center };
        _heroGroup = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _heroGroup.Children.Add(_hero);
        _name = Label(T("travel"), AppTypography.Title, FontWeight.Medium);
        _name.HorizontalAlignment = HorizontalAlignment.Center;
        _heroGroup.Children.Add(_name);
        var heroHint = Label(T("heroHint"), AppTypography.Label, muted: true);
        heroHint.HorizontalAlignment = HorizontalAlignment.Center; heroHint.Margin = new Thickness(0, 7, 0, 0);
        _heroGroup.Children.Add(heroHint); _showcase.Children.Add(_heroGroup);
        var examples = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var image = new Image { Width = 180, Height = 180 };
            _examples.Add(image);
            var card = new StackPanel { Margin = new Thickness(10, 0, 10, 15) };
            card.Children.Add(image);
            var choose = new AppButton { Content = T(_names[i]), Classes = { "ghost", "compact" }, HorizontalAlignment = HorizontalAlignment.Center };
            choose.Click += (_, _) => { _selected = index; Refresh(); };
            ToolTip.SetTip(choose, T("select")); card.Children.Add(choose);
            Grid.SetColumn(card, i % 2); Grid.SetRow(card, i / 2); examples.Children.Add(card);
        }
        Grid.SetColumn(examples, 1); _showcase.Children.Add(examples);
        var scroll = new ScrollViewer { Content = _showcase, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(12, 8, 12, 0) };
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var size in new[] { 48, 64, 96, 128 })
        {
            var image = new Image { Width = size, Height = size };
            _sizes.Add(image);
            var item = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Spacing = 3 };
            item.Children.Add(image);
            var caption = Label(size + " px", AppTypography.Caption, muted: true);
            caption.HorizontalAlignment = HorizontalAlignment.Center; item.Children.Add(caption); sizes.Children.Add(item);
        }
        footer.Children.Add(sizes);
        var tune = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        _frostValue = Label("", AppTypography.Label, muted: true);
        _blurValue = Label("", AppTypography.Label, muted: true);
        tune.Children.Add(SliderRow(T("frost"), _frost, _frostValue));
        tune.Children.Add(SliderRow(T("blur"), _blur, _blurValue));
        var fileActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileActions.Children.Add(AsyncAction(T("cover"), () => ChoosePhoto(false)));
        fileActions.Children.Add(AsyncAction(T("rear"), () => ChoosePhoto(true)));
        tune.Children.Add(fileActions);
        Grid.SetColumn(tune, 1); footer.Children.Add(tune);
        _error = Label("", AppTypography.Label, muted: true); _error.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_error, 1); Grid.SetColumnSpan(_error, 2); footer.Children.Add(_error);
        var note = Label(T("footer"), AppTypography.Caption, muted: true); note.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(note, 2); Grid.SetColumnSpan(note, 2); footer.Children.Add(note);
        Grid.SetRow(footer, 2); root.Children.Add(footer);

        _surface = new Border { Child = root };
        Content = _surface;
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
        foreach (var slider in new[] { _frost, _blur })
            slider.ValueChanged += (_, _) => { _refreshTimer.Stop(); _refreshTimer.Start(); };
        SizeChanged += (_, _) =>
        {
            var compact = ClientSize.Width < 950;
            _hero.Width = _hero.Height = compact ? 330 : 440;
            Grid.SetColumnSpan(_heroGroup, compact ? 2 : 1);
            Grid.SetRow(examples, compact ? 1 : 0); Grid.SetColumn(examples, compact ? 0 : 1);
            Grid.SetColumnSpan(examples, compact ? 2 : 1);
            Grid.SetColumn(tune, compact ? 0 : 1); Grid.SetRow(tune, compact ? 1 : 0);
            Grid.SetColumnSpan(tune, compact ? 2 : 1);
            Grid.SetRow(_error, compact ? 2 : 1); Grid.SetRow(note, compact ? 3 : 2);
            footer.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto,Auto,Auto" : "Auto,Auto,Auto");
            Grid.SetRow(actions, compact ? 1 : 0); Grid.SetColumn(actions, compact ? 0 : 1);
            Grid.SetColumnSpan(actions, compact ? 2 : 1);
            actions.Margin = compact ? new Thickness(0, 12, 0, 0) : default;
            Refresh();
        };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _refreshTimer.Stop();
            _refreshCancellation?.Cancel();
            foreach (var image in _leases.Keys) image.Source = null;
            foreach (var lease in _leases.Values) lease.Dispose();
            _leases.Clear();
            _cache.Dispose();
            _language.Dispose();
        };
        Refresh();
        Opened += async (_, _) =>
        {
            Refresh();
            if (Program.Option("--capture") is not { } path) return;
            Task pending;
            do { pending = _imageUpdates; await pending; } while (!ReferenceEquals(pending, _imageUpdates));
            if (_closed) return;
            await Task.Delay(80);
            using var capture = new RenderTargetBitmap(new PixelSize((int)ClientSize.Width * 2, (int)ClientSize.Height * 2), new Vector(192, 192));
            capture.Render(this); capture.Save(path);
            Console.WriteLine("CAPTURE " + path);
            if (Program.Arguments.Contains("--exit")) Close();
        };
    }

    private void Refresh()
    {
        if (_closed || _surface == null) return;
        RequestedThemeVariant = _dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _surface.Background = new SolidColorBrush(Color.Parse(_dark ? "#24272E" : "#ECEDEF"));
        Resources["TitleBarBackgroundBrush"] = _surface.Background;
        _name.Text = T(_names[_selected]);
        _frostValue.Text = $"{_frost.Value:0}%"; _blurValue.Text = $"{_blur.Value:0}";
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var material = new FolderCoverMaterial((float)_frost.Value / 100, (float)_blur.Value, _dark, _two);
        var generation = ++_generation;
        _imageUpdates = UpdateImagesAsync(material, generation, _refreshCancellation.Token);
    }

    private async Task UpdateImagesAsync(FolderCoverMaterial material, int generation, CancellationToken token)
    {
        try
        {
            var pending = _examples.Select((image, index) => UpdateImageAsync(image, index, material, generation, token)).ToList();
            pending.Add(UpdateImageAsync(_hero, _selected, material, generation, token));
            pending.AddRange(_sizes.Select(image => UpdateImageAsync(image, _selected, material, generation, token)));
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        { if (!_closed && generation == _generation) _error.Text = T("imageError"); }
    }

    private async Task UpdateImageAsync(Image image, int album, FolderCoverMaterial material, int generation, CancellationToken token)
    {
        var rearIndex = (album + 1) % _photos.Count;
        var cover = _photos[album];
        var rear = material.TwoPhotos ? _photos[rearIndex] : null;
        var scale = Program.Option("--capture") == null ? RenderScaling : Math.Max(2, RenderScaling);
        var pixels = Math.Clamp((int)Math.Ceiling(image.Width * scale / 32) * 32, 32, 1024);
        var entry = new FileSystemEntry
        {
            FullPath = $"/demo/album-{album}", IsDirectory = true,
            ThumbnailUrl = FormattableString.Invariant($"{_photoVersions[album]}:{(material.TwoPhotos ? _photoVersions[rearIndex] : 0)}:{material.Frost}:{material.Blur}:{material.Dark}:{material.TwoPhotos}")
        };
        var lease = _cache.TryAcquire(entry, pixels)
            ?? await _cache.AcquireAsync(entry, pixels,
                (_, size, ct) => _renderer.CreateAsync(cover, rear, size, material, ct), token);
        if (_closed || token.IsCancellationRequested || generation != _generation)
        { lease.Dispose(); return; }
        image.Source = lease.Bitmap;
        if (_leases.Remove(image, out var previous)) previous.Dispose();
        _leases[image] = lease;
    }

    private async Task ChoosePhoto(bool rear)
    {
        var targetIndex = rear ? (_selected + 1) % _photos.Count : _selected;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = T(rear ? "rear" : "cover"), AllowMultiple = false, FileTypeFilter = [FilePickerFileTypes.ImageAll] });
        if (files.Count == 0 || _closed) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            var length = stream.Length;
            if (length > 32L * 1024 * 1024) throw new InvalidDataException();
            var bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes);
            var normalized = await Task.Run(() =>
            {
                using var bitmap = FolderCoverRenderService.DecodePhoto(bytes, 1400, CancellationToken.None);
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 90);
                return png.ToArray();
            });
            if (_closed) return;
            _photos[targetIndex] = normalized;
            _photoVersions[targetIndex]++;
            if (rear) _imageCount.IsChecked = true;
            Refresh(); _error.Text = "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _error.Text = T("imageError"); }
    }

    private string T(string key) => _language.Get("demo.folder." + key);
    private static TextBlock Label(string text, string size, FontWeight? weight = null, bool muted = false)
    {
        var label = AppTypography.BindFontSize(new TextBlock { Text = text, FontWeight = weight ?? FontWeight.Normal }, size);
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(muted ? "TextMutedBrush" : "TextPrimaryBrush"));
        return label;
    }
    private static AppButton Action(string text, Action action)
    {
        var button = new AppButton { Content = text, Classes = { "secondary", "compact" } };
        button.Click += (_, _) => action(); return button;
    }
    private static AppButton AsyncAction(string text, Func<Task> action)
    {
        var button = new AppButton { Content = text, Classes = { "secondary", "compact" } };
        button.Click += async (_, _) => await action(); return button;
    }
    private static Control SliderRow(string text, Slider slider, TextBlock value)
    {
        AutomationProperties.SetName(slider, text);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("86,145,45") };
        var label = Label(text, AppTypography.Label); label.VerticalAlignment = VerticalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center; value.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(label); Grid.SetColumn(slider, 1); row.Children.Add(slider);
        Grid.SetColumn(value, 2); row.Children.Add(value); return row;
    }
}

internal sealed class DemoSettings(string language) : ISettingsService
{
    private readonly Dictionary<string, string> _values = new() { [LocalizationService.SettingKey] = language };
    public event Action<string>? SettingChanged;
    public string? Get(string key) => _values.GetValueOrDefault(key);
    public T Get<T>(string key, T defaultValue) => Get(key) is { } value ? System.Text.Json.JsonSerializer.Deserialize<T>(value)! : defaultValue;
    public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
    public void Set<T>(string key, T value) => Set(key, System.Text.Json.JsonSerializer.Serialize(value));
    public Dictionary<string, string> GetAll() => new(_values);
}
