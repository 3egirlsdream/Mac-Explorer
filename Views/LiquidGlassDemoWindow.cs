using Avalonia.Controls;
using Avalonia.Media;
using MacExplorer.Controls;

namespace MacExplorer.Views;

public class LiquidGlassDemoWindow : AppWindow
{
    public LiquidGlassDemoWindow()
    {
        Title = "Liquid Glass Demo";
        Width = 800;
        Height = 600;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#F2F2F7"));
        Content = new LiquidGlassDemo();
    }
}
