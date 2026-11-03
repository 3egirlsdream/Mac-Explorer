using MacExplorer.Controls;

namespace MacExplorer.Views;

public partial class MainWindow
{
    public override void Show()
    {
        if (WindowBackgroundTheme.GetController(this) is { } background)
            _ = background.ShowWhenReadyAsync(() =>
            {
                if (!_shutdownStarted) base.Show();
            });
        else
            base.Show();
    }
}
