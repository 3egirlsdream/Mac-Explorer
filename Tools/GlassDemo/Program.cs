using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using LiquidGlassAvaloniaUI;
using MacExplorer.Controls;

namespace GlassDemo;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<DemoApp>()
        .UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}

public sealed class DemoApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new DemoWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class DemoWindow : Window
{
    private readonly PatternBackdrop _pattern = new();
    private readonly Border _nativeSlot = new() { Background = Brushes.Transparent };
    private readonly Border _nativeCard = new() { Width = 290, Height = 360 };
    private readonly LiquidGlassSurface _libraryCard = new()
    {
        Width = 290, Height = 360, CornerRadius = new CornerRadius(22),
        TintColor = Colors.Transparent, SurfaceColor = Colors.Transparent,
        BlurRadius = 2, RefractionHeight = 18, RefractionAmount = 24,
        Vibrancy = 1, DepthEffect = true, HighlightOpacity = 0.5,
        HighlightWidth = 0.65, InnerShadowEnabled = false,
        ShadowEnabled = true, ShadowRadius = 4, ShadowOffset = new Vector(0, 1),
        ShadowColor = Color.Parse("#18000000")
    };
    private readonly TextBlock _status = Text("", 12);
    private readonly TextBlock _nativeHint = Text("", 12);
    private readonly List<Border> _solidSurfaces = [];
    private readonly List<TextBlock> _labels = [];
    private readonly List<Button> _rows = [];
    private readonly List<LiquidGlassInteractiveSurface> _rowGlass = [];
    private IntPtr _native, _view;
    private int _mode = 7;
    private Bitmap? _artwork;
    private (int Mode, bool Dark, int Width, int Height) _artworkKey;
    private bool _dark, _regular, _material, _enabled = true;
    private double _phase;

    public DemoWindow()
    {
        Title = "Glass Demo · 迁移前验证";
        Width = 1060; Height = 780; MinWidth = 900; MinHeight = 740;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        TransparencyBackgroundFallback = Brushes.Transparent;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,100") };
        var heading = new StackPanel { Spacing = 12, Margin = new Thickness(28, 16) };
        var title = Text("Glass Lab", 27, FontWeight.SemiBold);
        _labels.Add(title);
        heading.Children.Add(title);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(Action("选定样式", () => SetMode(7)));
        bar.Children.Add(Action("柔和弧面", () => SetMode(4)));
        bar.Children.Add(Action("低饱和柔光", () => SetMode(5)));
        bar.Children.Add(Action("极淡细纹", () => SetMode(6)));
        bar.Children.Add(Action("纯色", () => SetMode(0)));
        bar.Children.Add(Action("测试条纹", () => SetMode(1)));
        bar.Children.Add(Action("桌面", () => SetMode(2)));
        bar.Children.Add(Action("背后窗口", () => SetMode(3)));
        heading.Children.Add(bar);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(Action("浅色 / 深色", () => { _dark = !_dark; Refresh(); }));
        controls.Children.Add(Action("Clear / Regular", () => { _regular = !_regular; Refresh(); }));
        controls.Children.Add(Action("玻璃开 / 关", () => { _enabled = !_enabled; Refresh(); }));
        controls.Children.Add(Action("原生底材", () => { _material = !_material; Refresh(); }));
        controls.Children.Add(Action("移动测试条纹", () => { _phase = (_phase + 30) % 90; Refresh(); }));
        heading.Children.Add(controls);
        root.Children.Add(Solid(heading));

        var comparisons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        Grid.SetRow(comparisons, 1);
        var left = new Grid { RowDefinitions = new RowDefinitions("44,*,42") };
        var right = new Grid { RowDefinitions = new RowDefinitions("44,*,42") };
        left.Children.Add(Label("01   LiquidGlassAvaloniaUI", 14));
        right.Children.Add(Label("02   macOS 原生材质对照", 14));
        var leftArea = new Grid { ClipToBounds = false };
        leftArea.Children.Add(_pattern);
        _libraryCard.Content = CardContents("常用位置", true);
        leftArea.Children.Add(_libraryCard);
        Grid.SetRow(leftArea, 1);
        left.Children.Add(leftArea);
        _nativeCard.Child = CardContents("常用位置", false);
        _nativeCard.HorizontalAlignment = HorizontalAlignment.Center;
        _nativeCard.VerticalAlignment = VerticalAlignment.Center;
        _nativeSlot.Child = _nativeCard;
        Grid.SetRow(_nativeSlot, 1);
        right.Children.Add(_nativeSlot);
        var leftHint = Label("应用内采样 · 三种新背景与右侧共用图案", 12);
        _nativeHint.VerticalAlignment = VerticalAlignment.Center;
        _labels.Add(_nativeHint);
        var rightHint = Solid(_nativeHint, new Thickness(28, 0));
        Grid.SetRow(leftHint, 2); Grid.SetRow(rightHint, 2);
        left.Children.Add(leftHint); right.Children.Add(rightHint);
        comparisons.Children.Add(left);
        var gutter = Solid(null); Grid.SetColumn(gutter, 1); comparisons.Children.Add(gutter);
        Grid.SetColumn(right, 2); comparisons.Children.Add(right);
        root.Children.Add(comparisons);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(28, 16) };
        footer.Children.Add(_status); _labels.Add(_status);
        var note = Text("选定样式：浅色使用低饱和柔光；深色使用无亮边面板。Tab 查看焦点，点击列表查看选中。", 12);
        _labels.Add(note); footer.Children.Add(note);
        var footerSurface = Solid(footer); Grid.SetRow(footerSurface, 2); root.Children.Add(footerSurface);
        Content = root;
        Opened += (_, _) =>
        {
            if (OperatingSystem.IsMacOS() && TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle)
            {
                _view = handle.NSView;
                _native = Native.Create(_view);
            }
            Refresh();
            Console.WriteLine($"NATIVE created={_native != IntPtr.Zero} macOS={Environment.OSVersion.Version}");
        };
        LayoutUpdated += (_, _) => UpdateNative();
        Closed += (_, _) => { if (_native != IntPtr.Zero) Native.Destroy(_native); _native = IntPtr.Zero; _artwork?.Dispose(); };
        Refresh();
    }

    private Border Label(string value, double size)
    {
        var label = Text(value, size); _labels.Add(label);
        label.VerticalAlignment = VerticalAlignment.Center;
        return Solid(label, new Thickness(28, 0));
    }

    private Border Solid(Control? child, Thickness? padding = null)
    {
        var border = new Border { Child = child, Padding = padding ?? default };
        _solidSurfaces.Add(border); return border;
    }

    private Control CardContents(string title, bool interactive)
    {
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var caption = Text(title, 12, FontWeight.SemiBold);
        caption.Margin = new Thickness(10, 8, 0, 8); _labels.Add(caption);
        stack.Children.Add(caption);
        foreach (var label in new[] { "个人收藏", "最近使用", "下载", "文稿", "图片" })
        {
            var button = new Button
            {
                Content = label, Height = 42, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent, FocusAdorner = null,
                Theme = new ControlTheme(typeof(Button))
                {
                    Setters = { new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<Button>((b, _) =>
                    {
                        var presenter = new ContentPresenter
                        {
                            Content = b.Content, Margin = new Thickness(12, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Background = Brushes.Transparent
                        };
                        var outline = new Border { CornerRadius = new CornerRadius(10), Child = presenter,
                            Background = Brushes.Transparent, BorderThickness = new Thickness(1) };
                        void Update()
                        {
                            outline.BorderBrush = b.IsFocused ? Brush("#7C9DEB") : b.IsPointerOver && !_dark
                                ? Brush("#BBFFFFFF") : Brushes.Transparent;
                            outline.Background = _dark && b.IsPointerOver ? Brush("#0CFFFFFF") : Brushes.Transparent;
                        }
                        b.PointerEntered += (_, _) => Update(); b.PointerExited += (_, _) => Update();
                        b.GotFocus += (_, _) => Update(); b.LostFocus += (_, _) => Update();
                        return outline;
                    })) }
                }
            };
            _rows.Add(button);
            button.Click += (_, _) =>
            {
                foreach (var row in _rows) row.FontWeight = FontWeight.Normal;
                button.FontWeight = FontWeight.SemiBold;
                _status.Text = $"已选择：{label} · {(interactive ? "组件库" : "原生")} · 背景保持透明";
                Console.WriteLine($"CLICK {label} library={interactive}");
            };
            if (interactive)
            {
                var glass = new LiquidGlassInteractiveSurface
                {
                    Content = button, CornerRadius = new CornerRadius(10),
                    TintColor = Colors.Transparent, SurfaceColor = Colors.Transparent,
                    BlurRadius = 0, RefractionHeight = 8, RefractionAmount = 8, Vibrancy = 1,
                    BackdropOpacity = 0, HighlightOpacity = 0, ShadowEnabled = false, InnerShadowEnabled = false,
                    InteractiveMaxScaleDip = 0.8
                };
                button.PointerEntered += (_, _) => { glass.BackdropOpacity = _enabled && !_dark ? 1 : 0; glass.HighlightOpacity = _enabled && !_dark ? 0.35 : 0; };
                button.PointerExited += (_, _) => { glass.BackdropOpacity = 0; glass.HighlightOpacity = 0; };
                _rowGlass.Add(glass);
                stack.Children.Add(glass);
            }
            else stack.Children.Add(button);
        }
        return stack;
    }

    private static Button Action(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 7), FontSize = 12 };
        button.Click += (_, _) => action(); return button;
    }
    private void SetMode(int mode) { _mode = mode; Refresh(); }
    private void Refresh()
    {
        RequestedThemeVariant = _dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _nativeHint.Text = _dark ? "中性灰半透明面板 · 不绘制玻璃亮边" : OperatingSystem.IsMacOSVersionAtLeast(26)
            ? "NSGlassEffectView · 无额外染色" : "NSVisualEffectView 回退 · 非液态玻璃";
        foreach (var surface in _solidSurfaces) surface.Background = Brush(_dark ? "#1B1D21" : "#FFFFFF");
        foreach (var label in _labels) label.Foreground = Brush(_dark ? "#ECEFF4" : "#252932");
        foreach (var row in _rows) row.Foreground = Brush(_dark ? "#ECEFF4" : "#252932");
        _pattern.Mode = _mode; _pattern.Dark = _dark; _pattern.Phase = _phase; _pattern.InvalidateVisual();
        SidebarAppearance.Apply(_libraryCard, _dark);
        if (!_enabled)
        {
            _libraryCard.BackdropOpacity = 0;
            _libraryCard.SurfaceColor = Colors.Transparent;
            _libraryCard.RefractionAmount = 0;
            _libraryCard.HighlightOpacity = 0;
            _libraryCard.ShadowEnabled = false;
        }
        foreach (var glass in _rowGlass) { glass.BackdropOpacity = 0; glass.HighlightOpacity = 0; }
        _status.Text = $"背景：{new[] { "纯色", "测试条纹", "桌面 / 其他窗口", "独立测试窗口", "柔和弧面", "低饱和柔光", "极淡细纹", "选定样式" }[_mode]}    外观：{(_dark ? "深色 · 无亮边" : "浅色 · 柔光玻璃")}    原生：{(_dark ? "柔和面板" : _regular ? "Regular" : "Clear")}    玻璃：{(_enabled ? "开启" : "关闭")}";
        UpdateNative();
        Console.WriteLine($"STATE mode={_mode} dark={_dark} regular={_regular} enabled={_enabled} phase={_phase}");
    }
    private void UpdateNative()
    {
        if (_native == IntPtr.Zero || _nativeSlot.Bounds.Width <= 0) return;
        if (_mode >= 4)
        {
            var key = (Mode: _mode, Dark: _dark, Width: (int)Math.Ceiling(_nativeSlot.Bounds.Width * RenderScaling),
                Height: (int)Math.Ceiling(_nativeSlot.Bounds.Height * RenderScaling));
            if (key != _artworkKey && key.Width > 0 && key.Height > 0)
            {
                var png = BackgroundArt.Create(key.Mode, key.Dark, key.Width, key.Height);
                var previous = _artwork;
                using var stream = new MemoryStream(png);
                _artwork = new Bitmap(stream);
                _pattern.Artwork = _artwork;
                _pattern.InvalidateVisual();
                Native.SetArtwork(_native, png, png.Length);
                _artworkKey = key;
                previous?.Dispose();
            }
        }
        var p = _nativeSlot.TranslatePoint(default, this);
        var c = _nativeCard.TranslatePoint(default, _nativeSlot);
        if (p is null || c is null) return;
        Native.Update(_native, _view, p.Value.X, p.Value.Y, _nativeSlot.Bounds.Width, _nativeSlot.Bounds.Height,
            c.Value.X, c.Value.Y, _nativeCard.Bounds.Width, _nativeCard.Bounds.Height,
            _mode, _dark ? 1 : 0, _regular ? 1 : 0, _enabled ? 1 : 0, _material ? 1 : 0, _phase);
    }
    private static TextBlock Text(string value, double size, FontWeight? weight = null)
        => new() { Text = value, FontSize = size, FontWeight = weight ?? FontWeight.Normal };
    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}

internal sealed class PatternBackdrop : Control
{
    public Bitmap? Artwork { get; set; }
    public int Mode { get; set; }
    public bool Dark { get; set; }
    public double Phase { get; set; }
    public override void Render(DrawingContext context)
    {
        if (Mode is 2 or 3) return;
        if (Mode >= 4 && Artwork != null)
        {
            context.DrawImage(Artwork, new Rect(Bounds.Size));
            return;
        }
        context.FillRectangle(new SolidColorBrush(Dark ? Color.FromRgb(26, 31, 38) : Color.FromRgb(240, 242, 247)), new Rect(Bounds.Size));
        if (Mode == 0) return;
        var colors = new[] { Color.FromRgb(94, 168, 186), Color.FromRgb(237, 171, 112), Color.FromRgb(143, 145, 196) };
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            for (var i = -2; i < 12; i++)
                context.FillRectangle(new SolidColorBrush(colors[(i + 12) % 3]), new Rect(i * 90 + Phase, 0, 42, Bounds.Height));
            var pen = new Pen(new SolidColorBrush(Dark ? Color.FromArgb(31, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0)), 1);
            for (double y = 24; y < Bounds.Height; y += 36)
                context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
        }
    }
}

internal static class Native
{
    private const string Library = "GlassDemoNative";
    [DllImport(Library, EntryPoint = "glass_demo_create")] public static extern IntPtr Create(IntPtr view);
    [DllImport(Library, EntryPoint = "glass_demo_set_artwork")] public static extern void SetArtwork(IntPtr token, byte[] png, int length);
    [DllImport(Library, EntryPoint = "glass_demo_update")] public static extern void Update(IntPtr token, IntPtr view,
        double x, double y, double w, double h, double cx, double cy, double cw, double ch,
        int mode, int dark, int regular, int enabled, int material, double phase);
    [DllImport(Library, EntryPoint = "glass_demo_destroy")] public static extern void Destroy(IntPtr token);
}
