using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Interactivity;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;

namespace MacExplorer.Controls;

internal sealed class ShortcutHoldState
{
    private bool _commandDown;
    private bool _suppressed;
    private long _pressedAt;
    public bool IsWaiting { get; private set; }
    public bool ShouldShow { get; private set; }

    public void ModifiersChanged(KeyModifiers modifiers, long now)
    {
        var down = modifiers.HasFlag(KeyModifiers.Meta);
        if (!down) { Reset(); return; }
        if (!_commandDown)
        {
            _commandDown = true;
            _suppressed = modifiers != KeyModifiers.Meta;
            IsWaiting = !_suppressed;
            _pressedAt = now;
        }
        else if (modifiers != KeyModifiers.Meta) Interrupt();
    }
    public void Advance(long now)
    {
        if (IsWaiting && !_suppressed && now - _pressedAt >= 700)
        { IsWaiting = false; ShouldShow = true; }
    }
    public void Interrupt() { _suppressed = _commandDown; IsWaiting = false; ShouldShow = false; }
    public void Reset() { _commandDown = _suppressed = IsWaiting = ShouldShow = false; }
}

internal sealed class ShortcutHintController : IDisposable
{
    private readonly AppWindow _window;
    private readonly IShortcutService _shortcuts;
    private readonly ShortcutHoldState _hold = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long> _now;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly MacKeyboardMonitor? _native;
    private ShortcutHintView? _view;
    private bool _showing;
    private long _animationStart;
    private double _startOpacity;
    private double _startY;
    private bool _disposed;
    internal bool IsVisible => _view != null;

    public ShortcutHintController(AppWindow window, IShortcutService shortcuts, Func<long>? clock = null)
    {
        _window = window; _shortcuts = shortcuts;
        _now = clock ?? (() => _clock.ElapsedMilliseconds);
        _timer.Tick += OnTick;
        shortcuts.Changed += OnSettingsChanged;
        shortcuts.RecordingChanged += OnRecordingChanged;
        window.SizeChanged += OnSizeChanged;
        window.Deactivated += OnDeactivated;
        window.AddHandler(InputElement.KeyDownEvent, OnRoutedKeyDown, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.KeyUpEvent, OnRoutedKeyUp, RoutingStrategies.Tunnel, true);
        if (OperatingSystem.IsMacOS()) _native = new MacKeyboardMonitor(window, HandleActivity);
    }

    private void OnRoutedKeyDown(object? sender, KeyEventArgs e)
    {
        if (Services.Impl.ShortcutService.IsModifier(e.Key))
            HandleActivity(KeyboardActivity.Modifiers, e.Key, e.KeyModifiers | ModifierFor(e.Key), false);
        else
        {
            _hold.Interrupt();
            UpdateAnimation();
        }
    }

    private void OnRoutedKeyUp(object? sender, KeyEventArgs e)
    {
        if (Services.Impl.ShortcutService.IsModifier(e.Key))
            HandleActivity(KeyboardActivity.Modifiers, e.Key, e.KeyModifiers, false);
    }

    private static KeyModifiers ModifierFor(Key key) => key switch
    {
        Key.LWin or Key.RWin => KeyModifiers.Meta,
        Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
        Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
        Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
        _ => KeyModifiers.None
    };

    internal bool HandleActivity(KeyboardActivity activity, Key key, KeyModifiers modifiers, bool repeat)
    {
        if (Services.Subscriptions.SubscriptionAccess.IsLocked) { Cancel(); return false; }
        if (_disposed) return false;
        if (activity == KeyboardActivity.Deactivated) { Cancel(); return false; }
        if (activity == KeyboardActivity.KeyDown && _shortcuts.IsRecording)
        {
            Cancel();
            if (!repeat) _shortcuts.Capture(key, modifiers);
            return true;
        }
        if (!_window.IsActive || _shortcuts.IsRecording) { Cancel(); return false; }
        var handled = false;
        if (activity == KeyboardActivity.KeyDown && key == Key.Tab)
        {
            var args = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers,
                Source = _window.FocusManager?.GetFocusedElement() ?? _window
            };
            if (_shortcuts.Matches(ShortcutIds.NextTab, args) || _shortcuts.Matches(ShortcutIds.PreviousTab, args))
            {
                // AppKit intercepts Control+Tab before Avalonia's key route. Route it once here;
                // the native monitor consumes the original event only when a window handles it.
                _window.RaiseEvent(args);
                handled = args.Handled;
            }
        }
        var repeatedCommand = repeat && activity == KeyboardActivity.KeyDown && _shortcuts.Definitions.Any(d => d.IsEditable
            && _window.IsShortcutAvailable(d) && _shortcuts.GetBindings(d.Id).Any(b => b.Key == key && b.Modifiers == modifiers));
        if (!_shortcuts.HintsEnabled) { Cancel(); return handled || repeatedCommand; }
        if (activity == KeyboardActivity.Modifiers) _hold.ModifiersChanged(modifiers, _now());
        else if (activity is KeyboardActivity.KeyDown or KeyboardActivity.PointerDown) _hold.Interrupt();
        UpdateAnimation();
        if (_hold.IsWaiting || _view != null) _timer.Start();
        return handled || repeatedCommand;
    }

    private void OnTick(object? sender, EventArgs e)
        => AdvanceAnimation();

    internal void AdvanceAnimation()
    {
        if (_disposed) return;
        _hold.Advance(_now());
        UpdateAnimation();
        if (_view == null) { if (!_hold.IsWaiting) _timer.Stop(); return; }
        var duration = _showing ? 160d : 120d;
        var progress = Math.Clamp((_now() - _animationStart) / duration, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        _view.Opacity = _startOpacity + ((_showing ? 1 : 0) - _startOpacity) * eased;
        _view.Offset.Y = _showing ? _startY * (1 - eased) : _startY;
        if (progress < 1) return;
        if (!_showing) RemoveView();
        if (!_hold.IsWaiting) _timer.Stop();
    }

    private void UpdateAnimation()
    {
        var showing = _hold.ShouldShow;
        if (showing && _view == null && _window.WindowOverlayHost is { } host)
        {
            _view = new ShortcutHintView(_shortcuts) { Opacity = 0 };
            _view.Offset.Y = 6;
            ResizeView();
            host.Children.Add(_view);
        }
        if (_view == null || showing == _showing) return;
        _showing = showing;
        _view.IsHitTestVisible = showing;
        _startOpacity = _view.Opacity;
        _startY = _view.Offset.Y;
        _animationStart = _now();
        _timer.Start();
    }
    private void RemoveView()
    {
        if (_view != null) _window.WindowOverlayHost?.Children.Remove(_view);
        _view = null; _showing = false;
    }
    internal void Cancel() { _hold.Reset(); _timer.Stop(); RemoveView(); }
    private void OnDeactivated(object? sender, EventArgs e) => Cancel();
    private void OnSettingsChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(OnSettingsChanged); return; }
        if (!_shortcuts.HintsEnabled) Cancel();
        else _view?.Refresh();
    }
    private void OnRecordingChanged() { if (_shortcuts.IsRecording) Cancel(); }
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ResizeView();
    private void ResizeView()
    {
        if (_view == null) return;
        _view.Width = Math.Min(960, Math.Max(200, _window.Bounds.Width - 40));
        _view.MaxHeight = Math.Max(100, _window.Bounds.Height - 48);
        _view.SetColumns(_view.Width >= 860 ? 3 : _view.Width >= 580 ? 2 : 1);
    }
    public void Dispose()
    {
        _disposed = true; Cancel(); _native?.Dispose();
        _timer.Tick -= OnTick;
        _shortcuts.Changed -= OnSettingsChanged;
        _shortcuts.RecordingChanged -= OnRecordingChanged;
        _window.SizeChanged -= OnSizeChanged;
        _window.Deactivated -= OnDeactivated;
        _window.RemoveHandler(InputElement.KeyDownEvent, OnRoutedKeyDown);
        _window.RemoveHandler(InputElement.KeyUpEvent, OnRoutedKeyUp);
    }
}

internal sealed class ShortcutHintView : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly IShortcutService _shortcuts;
    private readonly Grid _groups = new() { ColumnSpacing = 24 };
    private int _columns = 1;
    internal TranslateTransform Offset { get; } = new();

    public ShortcutHintView(IShortcutService shortcuts)
    {
        _shortcuts = shortcuts;
        Name = "ShortcutHint";
        Classes.Add("shortcut-hint");
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        RenderTransform = Offset;
        Focusable = false;
        var content = new ScrollViewer { Content = _groups, Focusable = false, AllowAutoHide = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.None);
        Child = content;
        PopupGlass.SetIsEnabled(this, true);
        Refresh();
    }
    internal void SetColumns(int columns) { if (_columns == columns) return; _columns = columns; Refresh(); }
    internal void Refresh()
    {
        _groups.Children.Clear();
        _groups.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", _columns)));
        var columns = Enumerable.Range(0, _columns).Select(_ => new StackPanel { Spacing = 14 }).ToArray();
        for (var i = 0; i < columns.Length; i++) { Grid.SetColumn(columns[i], i); _groups.Children.Add(columns[i]); }
        var heights = new int[_columns];
        foreach (var group in _shortcuts.Definitions.Where(d => d.Scope != ShortcutScope.Editor).GroupBy(d => d.Group))
        {
            var section = new StackPanel { Spacing = 3 };
            section.Children.Add(new TextBlock { Text = group.Key, Classes = { "shortcut-group-title" }, Margin = new Thickness(0, 0, 0, 4) });
            foreach (var definition in group)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8,
                    MinHeight = 25 };
                var name = new TextBlock { Text = definition.Context == null ? definition.Name : $"{definition.Name} · {definition.Context}", TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center, Classes = { "shortcut-name" } };
                ToolTip.SetTip(name, definition.Context == null ? definition.Name : $"{definition.Name} · {definition.Context}");
                var key = new ShortcutText { Text = string.Join(" / ", _shortcuts.GetBindings(definition.Id)
                    .Select(b => b.Display)),
                    VerticalAlignment = VerticalAlignment.Center, Classes = { "shortcut-keys" } };
                Grid.SetColumn(key, 1); row.Children.Add(name); row.Children.Add(key); section.Children.Add(row);
            }
            var index = Array.IndexOf(heights, heights.Min());
            columns[index].Children.Add(section);
            heights[index] += group.Count() + 2;
        }
    }
}
