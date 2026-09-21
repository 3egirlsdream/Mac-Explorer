using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MacExplorer.Controls;

/// <summary>A shared 2-DIP caret for Fluent inputs; Avalonia 12 fixes its built-in caret at 1 DIP.</summary>
public sealed class InputCaret : Control
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<InputCaret, TextPresenter, bool>("Enabled");

    private readonly DispatcherTimer _timer = new();
    private TextPresenter? _presenter;
    private TextBox? _textBox;
    private bool _visible;
    private IDisposable? _caretOverride;

    static InputCaret()
    {
        EnabledProperty.Changed.AddClassHandler<TextPresenter>((presenter, change) =>
            AdornerLayer.SetAdorner(presenter, change.GetNewValue<bool>() ? new InputCaret() : null));
    }

    public InputCaret()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => { _visible = !_visible; InvalidateVisual(); };
    }

    public static bool GetEnabled(TextPresenter presenter) => presenter.GetValue(EnabledProperty);
    public static void SetEnabled(TextPresenter presenter, bool value) => presenter.SetValue(EnabledProperty, value);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _presenter = AdornerLayer.GetAdornedElement(this) as TextPresenter;
        _textBox = _presenter?.GetVisualAncestors().OfType<TextBox>().FirstOrDefault();
        if (_presenter == null || _textBox == null) return;

        _caretOverride = _presenter.SetValue(TextPresenter.CaretBrushProperty, Brushes.Transparent, BindingPriority.StyleTrigger);
        _presenter.CaretBoundsChanged += CaretChanged;
        _presenter.PropertyChanged += PresenterChanged;
        _textBox.GotFocus += FocusChanged;
        _textBox.LostFocus += FocusChanged;
        _textBox.PropertyChanged += TextBoxChanged;
        ResetBlink();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        if (_presenter != null)
        {
            _presenter.CaretBoundsChanged -= CaretChanged;
            _presenter.PropertyChanged -= PresenterChanged;
            _caretOverride?.Dispose();
            _caretOverride = null;
        }
        if (_textBox != null)
        {
            _textBox.GotFocus -= FocusChanged;
            _textBox.LostFocus -= FocusChanged;
            _textBox.PropertyChanged -= TextBoxChanged;
        }
        _presenter = null;
        _textBox = null;
        base.OnDetachedFromVisualTree(e);
    }

    // Text layout can update caret bounds while the compositor is rendering.
    // Queue the redraw after that pass instead of invalidating a visual inside it.
    private void CaretChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(ResetBlink, DispatcherPriority.Background);
    private void FocusChanged(object? sender, RoutedEventArgs e) => ResetBlink();
    private void TextBoxChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.IsReadOnlyProperty || e.Property == TextBox.CaretBrushProperty)
            ResetBlink();
    }

    private void PresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextPresenter.CaretIndexProperty || e.Property == TextPresenter.SelectionStartProperty ||
            e.Property == TextPresenter.SelectionEndProperty || e.Property == TextPresenter.TextProperty ||
            e.Property == TextPresenter.PreeditTextProperty || e.Property == TextPresenter.PreeditTextCursorPositionProperty ||
            e.Property == TextPresenter.CaretBlinkIntervalProperty)
            ResetBlink();
    }

    private void ResetBlink()
    {
        _timer.Stop();
        _visible = _textBox is { IsFocused: true, IsReadOnly: false };
        if (_visible && _presenter is { CaretBlinkInterval.TotalMilliseconds: > 0 })
        {
            _timer.Interval = _presenter.CaretBlinkInterval;
            _timer.Start();
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!_visible || _presenter == null || _textBox == null ||
            _presenter.SelectionStart != _presenter.SelectionEnd) return;

        var index = _presenter.CaretIndex;
        if (_presenter.PreeditText is { Length: > 0 } preedit)
            index += _presenter.PreeditTextCursorPosition is int cursor && cursor >= 0 && cursor <= preedit.Length
                ? cursor : preedit.Length;
        var bounds = _presenter.TextLayout.HitTestTextPosition(index);
        var x = Math.Clamp(Math.Floor(bounds.X), 0, Math.Max(0, Bounds.Width - 2));
        context.FillRectangle(_textBox.CaretBrush ?? Brushes.DodgerBlue,
            new Rect(x, Math.Floor(bounds.Y), 2, Math.Ceiling(bounds.Height)));
    }
}
