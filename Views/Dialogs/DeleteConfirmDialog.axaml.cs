using Avalonia.Controls;
using Avalonia.Interactivity;
using MacExplorer.Controls;

namespace MacExplorer.Views.Dialogs;

public partial class DeleteConfirmDialog : DialogWindow
{
    public DeleteConfirmDialog()
    {
        InitializeComponent();
    }

    public void SetMessage(string message)
    {
        ConfirmMessage.Text = message;
    }

    public void Configure(string title, string message, string confirmText)
    {
        Title = title;
        DialogTitle.Text = title;
        ConfirmMessage.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void Cancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void Confirm(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    public Task<bool> ShowDialogAsync(Window owner) => base.ShowDialog<bool>(owner);
}
