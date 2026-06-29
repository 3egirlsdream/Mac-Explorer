using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Models;
using AssetsIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Views.Dialogs;

public partial class CompressDialog : DialogWindow
{
    private string _format = "Zip";
    private string _level = "Standard";
    private bool _passwordVisible;
    private CompressOptions? _options;

    public CompressDialog()
    {
        InitializeComponent();
    }

    public Task<CompressOptions?> ShowDialogAsync(Window owner, CompressOptions options)
    {
        _options = options;
        ArchiveNameBox.Text = options.ArchiveName;
        _format = options.Format.ToString();
        _level = options.Level.ToString();
        PasswordBox.Text = options.Password ?? "";
        UpdateSelectionClasses();
        UpdatePasswordSection();
        return base.ShowDialog<CompressOptions?>(owner);
    }

    private void OnFormatChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _format = tag;
        UpdateSelectionClasses();
        UpdatePasswordSection();
    }

    private void OnLevelChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _level = tag;
        UpdateSelectionClasses();
    }

    private void UpdateSelectionClasses()
    {
        SetSegmentSelected(BtnZip, _format == "Zip");
        SetSegmentSelected(BtnTarGz, _format == "TarGz");
        SetSegmentSelected(BtnTarBz2, _format == "TarBz2");
        SetSegmentSelected(BtnFast, _level == "Fast");
        SetSegmentSelected(BtnStandard, _level == "Standard");
        SetSegmentSelected(BtnMaximum, _level == "Maximum");
    }

    private void UpdatePasswordSection()
    {
        var isZip = _format == "Zip";
        PasswordSection.IsVisible = isZip;
    }

    private static void SetSegmentSelected(Button button, bool selected)
    {
        button.Classes.Set("secondary", !selected);
        button.Classes.Set("primary", selected);
    }

    private void TogglePasswordVisibility(object? sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        PasswordBox.PasswordChar = _passwordVisible ? default : '●';

        var icon = new PathIcon
        {
            Data = _passwordVisible
                ? Geometry.Parse(AssetsIcons.PasswordHidden)
                : Geometry.Parse(AssetsIcons.Eye),
            Width = 16,
            Height = 16
        };
        TogglePasswordBtn.Content = icon;
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        var name = ArchiveNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (_options == null) return;

        var password = PasswordBox.Text;
        var result = new CompressOptions
        {
            ArchiveName = name,
            Format = Enum.Parse<ArchiveFormat>(_format),
            Level = Enum.Parse<CompressionLevel>(_level),
            OutputDirectory = _options.OutputDirectory,
            SourcePaths = _options.SourcePaths,
            CollectionId = _options.CollectionId,
            Password = string.IsNullOrEmpty(password) ? null : password
        };
        Close(result);
    }
}
