using Avalonia.Controls;
using Avalonia.Interactivity;
using MacExplorer.Controls;
using MacExplorer.Services;

namespace MacExplorer.Views.Dialogs;

public partial class ImageConversionDialog : DialogWindow
{
    private double _ratio = 1;
    private bool _updating = true;
    public ImageConversionDialog() { InitializeComponent(); }

    public ImageConversionDialog(ConversionImageSize size, FileConversionFormat format) : this()
    {
        _ratio = (double)size.Width / size.Height;
        Heading.Text = "转为 " + format.ToString().ToUpperInvariant();
        Description.Text = format == FileConversionFormat.Jpg ? "透明区域使用白色背景，图片质量为 90%。" : "保留原图的透明背景。";
        var scale = Math.Min(1d, Math.Min(8192d / Math.Max(size.Width, size.Height), Math.Sqrt(32_000_000d / ((double)size.Width * size.Height))));
        WidthInput.Value = Math.Max(1, (int)Math.Floor(size.Width * scale));
        HeightInput.Value = Math.Max(1, (int)Math.Floor(size.Height * scale));
        _updating = false;
    }

    private void WidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || WidthInput.Value == null) return;
        _updating = true;
        HeightInput.Value = Math.Clamp((decimal)Math.Round((double)WidthInput.Value / _ratio), 1, 8192);
        _updating = false;
    }

    private void HeightChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || HeightInput.Value == null) return;
        _updating = true;
        WidthInput.Value = Math.Clamp((decimal)Math.Round((double)HeightInput.Value * _ratio), 1, 8192);
        _updating = false;
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
    private void ConvertClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (WidthInput.Value == null || HeightInput.Value == null) throw new InvalidOperationException("请输入宽度和高度。");
            var size = new ConversionImageSize((int)WidthInput.Value, (int)HeightInput.Value);
            size.Validate();
            var expected = size.Width / _ratio;
            if (Math.Abs(size.Height - expected) > 1) throw new InvalidOperationException("此尺寸超出限制，请减小尺寸以保持原图比例。");
            Close(size);
        }
        catch (InvalidOperationException ex) { ErrorText.Text = ex.Message; ErrorText.IsVisible = true; }
    }
}
