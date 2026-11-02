using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Shared compact palette: visible swatches, safe hex drafts and explicit reset.</summary>
public partial class ColorPalette : UserControl
{
    private bool _updating;
    private bool _allowAlpha = true;
    private string _color = "#FFFFFF";
    private string[] _choices = [];
    public event Action<string>? ColorChanged;
    public event Action? ResetRequested;
    public TextBox InputBox => ColorInput;
    public TextBlock DescriptionBlock => DescriptionText;
    public string SelectedColor => _color;

    public ColorPalette() => InitializeComponent();

    public void Configure(IEnumerable<string> colors, string selected, string? description = null, bool allowAlpha = true)
    {
        _allowAlpha = allowAlpha;
        ColorInput.PlaceholderText = allowAlpha ? "#RRGGBB / #AARRGGBB" : "#RRGGBB";
        var choices = colors.ToArray();
        if (!_choices.SequenceEqual(choices))
        {
            _choices = choices;
            Swatches.Children.Clear();
            foreach (var value in choices)
            {
                var dot = new Border { Classes = { "color-palette-dot" }, Background = new SolidColorBrush(Color.Parse(value)) };
                var button = new Button { Classes = { "ghost", "color-palette-swatch" }, Tag = value,
                    Content = dot, Margin = new(0, 0, 6, 6) };
                ToolTip.SetTip(button, value);
                AutomationProperties.SetName(button, value);
                button.Click += (_, _) => SelectColor(value);
                Swatches.Children.Add(button);
            }
        }
        DescriptionText.Text = description;
        DescriptionText.IsVisible = !string.IsNullOrEmpty(description);
        SetSelection(selected);
    }

    public void SetSelection(string color)
    {
        if (!TryNormalize(color, out var normalized)) return;
        _color = normalized;
        SetInput(normalized);
        ErrorText.IsVisible = false;
        UpdateSelection();
    }

    private void SelectColor(string color)
    {
        if (!TryNormalize(color, out var normalized)) return;
        SetSelection(normalized);
        ColorChanged?.Invoke(normalized);
    }

    private void InputChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        // Hex input is a draft until Enter or blur. In particular, seven
        // characters may still be the beginning of an eight-digit alpha color.
        ErrorText.IsVisible = false;
    }

    private void InputCommitted(object? sender, RoutedEventArgs e) => CommitInput();
    private void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitInput(); e.Handled = true; }
        else if (e.Key == Key.Escape && ColorInput.Text != _color) { SetSelection(_color); e.Handled = true; }
    }

    private void CommitInput()
    {
        if (TryNormalize(ColorInput.Text, out var normalized))
        {
            if (_color != normalized) { _color = normalized; UpdateSelection(); ColorChanged?.Invoke(normalized); }
            SetInput(normalized, preserveCaret: true); ErrorText.IsVisible = false;
        }
        else
        {
            ErrorText.Text = LocalizationText.Get(_allowAlpha
                ? "颜色格式无效，请使用 #RRGGBB 或 #AARRGGBB。" : "background.invalid_color");
            ErrorText.IsVisible = true;
        }
    }

    private void SetInput(string value, bool preserveCaret = false)
    {
        _updating = true;
        try
        {
            var caret = ColorInput.CaretIndex;
            if (ColorInput.Text != value) ColorInput.Text = value;
            if (preserveCaret) ColorInput.CaretIndex = Math.Min(caret, value.Length);
        }
        finally { _updating = false; }
    }

    private bool TryNormalize(string? value, out string normalized)
    {
        value = value?.Trim();
        normalized = "";
        if (value == null || (value.Length != 7 && (!_allowAlpha || value.Length != 9)) || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _) || !Color.TryParse(value, out var color)) return false;
        normalized = color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        return true;
    }

    private void UpdateSelection()
    {
        foreach (var button in Swatches.Children.OfType<Button>())
            button.Classes.Set("active", TryNormalize((string?)button.Tag, out var value) && value == _color);
    }

    private void ResetClicked(object? sender, RoutedEventArgs e) => ResetRequested?.Invoke();
}
