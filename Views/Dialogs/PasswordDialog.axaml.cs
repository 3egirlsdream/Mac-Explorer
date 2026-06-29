using Avalonia.Controls;
using Avalonia.Interactivity;
using MacExplorer.Assets;
using MacExplorer.Controls;

namespace MacExplorer.Views.Dialogs;

public partial class PasswordDialog : DialogWindow
{
    private bool _passwordVisible;

    public PasswordDialog()
    {
        InitializeComponent();
        Opened += (_, _) => PasswordBox.Focus();
    }

    public Task<string?> ShowDialogAsync(Window owner)
    {
        return base.ShowDialog<string?>(owner);
    }

    private void TogglePasswordVisibility(object? sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        PasswordBox.PasswordChar = _passwordVisible ? default : '●';

        var icon = new Avalonia.Controls.PathIcon
        {
            Data = _passwordVisible
                ? Avalonia.Media.Geometry.Parse(Icons.PasswordHidden)
                : Avalonia.Media.Geometry.Parse(Icons.Eye),
            Width = 16,
            Height = 16
        };
        ToggleVisibilityBtn.Content = icon;
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text;
        Close(string.IsNullOrEmpty(password) ? null : password);
    }
}
