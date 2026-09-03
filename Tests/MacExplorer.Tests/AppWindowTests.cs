using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public class AppWindowTests
{
    [AvaloniaFact]
    public void TitleBarWithoutContentUsesSingleRowAndShowsTitle()
    {
        var window = new AppWindow { Width = 480, Height = 320, Title = "设置" };
        window.Show();
        var titleBar = window.GetVisualDescendants().OfType<WindowTitleBar>().Single();
        var titleText = titleBar.FindControl<TextBlock>("TitleText")!;
        var contentRow = titleBar.FindControl<Border>("TitleBarContentRow")!;

        Assert.Equal(44, titleBar.Bounds.Height);
        Assert.False(contentRow.IsVisible);
        Assert.True(titleText.IsVisible);
        Assert.Equal("设置", titleText.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void TitleBarWithContentSharesTheTrafficLightRowAndReplacesTheTitle()
    {
        var content = new Border();
        var window = new AppWindow
        {
            Width = 480,
            Height = 320,
            Title = "文稿",
            TitleBarContent = content
        };
        window.Show();
        var titleBar = window.GetVisualDescendants().OfType<WindowTitleBar>().Single();
        var titleText = titleBar.FindControl<TextBlock>("TitleText")!;
        var contentRow = titleBar.FindControl<Border>("TitleBarContentRow")!;

        Assert.Equal(44, titleBar.Bounds.Height);
        Assert.True(contentRow.IsVisible);
        Assert.True(contentRow.ClipToBounds);
        Assert.False(titleText.IsVisible);
        Assert.Equal("文稿", titleText.Text);
        Assert.Same(content, titleBar.TitleBarContent);
        window.Close();
    }

    [AvaloniaFact]
    public void FullScreenToggleRestoresNormalState()
    {
        var window = new AppWindow { CanMaximize = true, WindowState = WindowState.Normal };

        window.ToggleFullScreen();
        Assert.Equal(WindowState.FullScreen, window.WindowState);

        window.ToggleFullScreen();
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public void FullScreenToggleRestoresMaximizedState()
    {
        var window = new AppWindow { CanMaximize = true, WindowState = WindowState.Maximized };

        window.ToggleFullScreen();
        window.ToggleFullScreen();

        Assert.Equal(WindowState.Maximized, window.WindowState);
    }

    [AvaloniaFact]
    public void ModalBlockPreventsFullScreenToggle()
    {
        var window = new AppWindow
        {
            CanMaximize = true,
            IsModalInteractionBlocked = true,
            WindowState = WindowState.Normal
        };

        window.ToggleFullScreen();

        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public void WindowCapabilitiesUpdateTrafficLightButtons()
    {
        var titleBar = new WindowTitleBar();
        var window = new AppWindow { Content = titleBar, CanMinimize = true, CanMaximize = true };
        window.Show();
        var minimize = titleBar.FindControl<Button>("MinimizeButton")!;
        var fullScreen = titleBar.FindControl<Button>("FullScreenButton")!;

        window.CanMinimize = false;
        window.CanMaximize = false;

        Assert.False(minimize.IsEnabled);
        Assert.False(fullScreen.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void ModalBlockDisablesAllTrafficLightButtons()
    {
        var titleBar = new WindowTitleBar();
        var window = new AppWindow { Content = titleBar };
        window.Show();

        window.IsModalInteractionBlocked = true;

        Assert.All(
            new[]
            {
                titleBar.FindControl<Button>("CloseButton")!,
                titleBar.FindControl<Button>("MinimizeButton")!,
                titleBar.FindControl<Button>("FullScreenButton")!
            },
            button => Assert.False(button.IsEnabled));
        window.Close();
    }

    [AvaloniaFact]
    public void FullScreenTemplateKeepsExitButtonAvailable()
    {
        var window = new AppWindow { CanMaximize = true };
        window.Show();
        var titleBar = window.GetVisualDescendants().OfType<WindowTitleBar>().Single();
        var fullScreenButton = titleBar.FindControl<Button>("FullScreenButton")!;

        window.ToggleFullScreen();

        Assert.True(titleBar.IsVisible);
        Assert.Equal("退出全屏", AutomationProperties.GetName(fullScreenButton));
        fullScreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Normal, window.WindowState);
        window.Close();
    }
}
