using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public class AppWindowTests
{
    [AvaloniaTheory]
    [InlineData("MinimizeButton")]
    [InlineData("FullScreenButton")]
    public void HoveringDisabledDialogControlsKeepsCloseSymbolVisible(string buttonName)
    {
        var window = new DialogWindow { Width = 480, Height = 320 };
        window.Show();
        try
        {
            var titleBar = window.GetVisualDescendants().OfType<WindowTitleBar>().Single();
            var group = titleBar.GetVisualDescendants().OfType<StackPanel>()
                .Single(panel => panel.Classes.Contains("window-controls"));
            var disabledButton = titleBar.FindControl<Button>(buttonName)!;
            var closeButton = titleBar.FindControl<Button>("CloseButton")!;
            var closeSymbol = closeButton.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
            var disabledSymbol = disabledButton.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
            var point = disabledButton.TranslatePoint(
                new Point(disabledButton.Bounds.Width / 2, disabledButton.Bounds.Height / 2), window)!.Value;
            var opacityChanges = 0;
            closeSymbol.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.OpacityProperty)
                    opacityChanges++;
            };

            Assert.False(disabledButton.IsEnabled);
            for (var frame = 0; frame < 30; frame++)
            {
                window.MouseMove(point);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.Same(group, window.InputHitTest(point));
                Assert.True(group.IsPointerOver);
                Assert.Equal(1, closeSymbol.Opacity);
                Assert.Equal(0, disabledSymbol.Opacity);
            }
            Assert.Equal(1, opacityChanges);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(window.IsVisible);

            window.MouseMove(new Point(200, 100));
            Assert.Equal(0, closeSymbol.Opacity);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TitleBarPopupIsHostedInsideTheWindowAndKeepsItsPositionWhenWindowMoves()
    {
        var target = new Border { Width = 28, Height = 28 };
        var window = new AppWindow { Width = 640, Height = 480, TitleBarContent = target };
        window.Show();
        var content = new Border { Width = 320, Height = 240 };
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ShouldUseOverlayLayer = true,
            Child = content
        };

        popup.IsOpen = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(popup.IsUsingOverlayLayer);
        var host = window.GetVisualDescendants().OfType<OverlayPopupHost>().Single();
        Assert.Same(window, TopLevel.GetTopLevel(host));
        var relativePosition = host.TranslatePoint(default, window);
        Assert.NotNull(relativePosition);

        window.Position = new Avalonia.PixelPoint(150, 120);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(popup.IsOpen);
        Assert.Equal(relativePosition, host.TranslatePoint(default, window));
        popup.IsOpen = false;
        window.Close();
    }

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
