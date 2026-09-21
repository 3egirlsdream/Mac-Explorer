using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Views;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed class InputAppearanceTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DropdownStaysFlatAndSharesContextMenuSurface(bool dark)
    {
        var combo = new ComboBox { Width = 220, ItemsSource = new[] { "第一项", "第二项", "第三项" }, SelectedIndex = 0 };
        var menu = new Border { Classes = { "context-submenu-surface" } };
        var window = CreateWindow(new StackPanel { Children = { combo, menu } }, dark);
        try
        {
            var background = combo.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "Background");
            var highlight = combo.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "HighlightBackground");
            var resting = background.Background;
            var point = combo.TranslatePoint(new Point(20, 15), window)!.Value;
            window.MouseMove(point);
            Assert.Equal(resting, background.Background);
            window.MouseDown(point, MouseButton.Left);
            Assert.Equal(resting, background.Background);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(combo.IsDropDownOpen);
            Assert.Equal(resting, background.Background);
            Assert.Equal(new Thickness(0), background.BorderThickness);
            var popup = combo.GetVisualDescendants().OfType<Popup>().Single();
            var surface = Assert.IsType<Border>(popup.Child);
            Assert.Equal(menu.Background, surface.Background);
            Assert.Equal(menu.BorderThickness, surface.BorderThickness);
            Assert.Equal(menu.CornerRadius, surface.CornerRadius);
            Assert.Equal(menu.Padding, surface.Padding);
            Assert.Equal(menu.Margin, surface.Margin);
            Assert.Equal(menu.BoxShadow, surface.BoxShadow);
            Assert.True(surface.Bounds.Width >= combo.Bounds.Width);
            Assert.False(popup.WindowManagerAddShadowHint);
            combo.IsDropDownOpen = false;
            combo.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.False(highlight.IsVisible);
            Assert.Equal(resting, background.Background);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Equal(1, combo.SelectedIndex);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickingInputDoesNotChangeItsSurface(bool dark)
    {
        var input = new TextBox { Width = 220, Height = 40, Text = "hello" };
        var window = CreateWindow(new StackPanel { Children = { input, new TextBox() } }, dark);
        try
        {
            var border = input.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_BorderElement");
            var background = border.Background;
            var point = input.TranslatePoint(new Point(30, 20), window)!.Value;
            window.MouseMove(point);
            Assert.Equal(background, border.Background);
            window.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(background, border.Background);
            window.MouseUp(point, MouseButton.Left);
            window.MouseMove(new Point(390, 240));
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.IsFocused);
            Assert.Equal(background, border.Background);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void HomeSearchSurfaceStaysUnchangedThroughHoverPressAndFocus(bool dark)
    {
        var home = new HomeView();
        var window = CreateWindow(home, dark);
        try
        {
            var input = home.FindControl<TextBox>("HomeSearchBox")!;
            var surface = home.FindControl<Border>("HomeSearchContainer")!;
            var border = input.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_BorderElement");
            var resting = surface.Background;
            var iconPoint = surface.TranslatePoint(new Point(20, 20), window)!.Value;
            var inputPoint = input.TranslatePoint(new Point(20, 20), window)!.Value;
            window.MouseMove(iconPoint);
            var hover = surface.Background;
            Assert.Equal(resting, hover);
            window.MouseMove(inputPoint);
            Assert.Equal(hover, surface.Background);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)border.Background!).Color);
            window.MouseDown(inputPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(resting, surface.Background);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)border.Background!).Color);
            window.MouseUp(inputPoint, MouseButton.Left);
            Assert.Equal(resting, surface.Background);
            window.MouseMove(new Point(390, 240));
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.IsFocused);
            Assert.Equal(resting, surface.Background);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)border.Background!).Color);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputsStayBorderlessThroughFocusTypingAndSelection(bool dark)
    {
        var input = new TextBox { Width = 220, Text = "hello" };
        var other = new TextBox { Classes = { "search-input" } };
        var panel = new StackPanel { Children = { input, other } };
        var window = CreateWindow(panel, dark);
        try
        {
            input.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.GetVisualDescendants().OfType<Border>().Any(x => x.Name == "PART_BorderElement"), string.Join(", ", input.GetVisualDescendants().Select(x => $"{x.GetType().Name}:{(x as Control)?.Name}")));
            var border = input.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_BorderElement");
            var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
            Assert.Equal(new Thickness(0), border.BorderThickness);
            Assert.Equal(input.Background, border.Background);
            Assert.Null(input.FocusAdorner);
            Assert.IsType<InputCaret>(AdornerLayer.GetAdorner(presenter));
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)presenter.CaretBrush!).Color);
            Assert.Equal(Color.Parse(dark ? "#0A84FF" : "#007AFF"), ((ISolidColorBrush)input.CaretBrush!).Color);

            input.CaretIndex = input.Text!.Length;
            window.KeyTextInput(" world");
            Assert.Equal("hello world", input.Text);
            input.SelectAll();
            Assert.Equal(input.Text.Length, Math.Abs(input.SelectionEnd - input.SelectionStart));
            other.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Thickness(0), border.BorderThickness);
            Assert.Equal(input.Background, border.Background);

            // Removing/reinserting a cached input must release and restore its caret.
            panel.Children.Remove(input);
            Assert.NotEqual(Colors.Transparent, (presenter.CaretBrush as ISolidColorBrush)?.Color);
            panel.Children.Add(input);
            input.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)presenter.CaretBrush!).Color);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckboxKeepsBorderlessFillAcrossAllThreeStates(bool dark)
    {
        var check = new CheckBox { IsThreeState = true, Content = "选项" };
        var window = CreateWindow(check, dark);
        try
        {
            Assert.True(check.GetVisualDescendants().OfType<Border>().Any(x => x.Name == "NormalRectangle"), string.Join(", ", check.GetVisualDescendants().Select(x => $"{x.GetType().Name}:{(x as Control)?.Name}")));
            var border = check.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "NormalRectangle");
            Assert.Equal(Color.Parse(dark ? "#454545" : "#E9E9E9"), ((ISolidColorBrush)border.Background!).Color);
            Assert.Equal(new Thickness(0), border.BorderThickness);
            Assert.Equal(16, border.Width);
            foreach (var state in new bool?[] { true, null, false })
            {
                check.IsChecked = state;
                Assert.Equal(new Thickness(0), border.BorderThickness);
                Assert.Equal(16, border.Width);
                if (state != false)
                    Assert.Equal(Color.Parse(dark ? "#0A84FF" : "#007AFF"), ((ISolidColorBrush)border.Background!).Color);
            }
        }
        finally { window.Close(); }
    }

    internal static Window CreateWindow(Control content, bool dark, double width = 400)
    {
        var window = new Window { Width = width, Height = 600, Content = content,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        var fluent = new FluentTheme();
        Application.Current!.Styles.Insert(0, fluent);
        var sharedStyles = new List<IStyle> { fluent };
        foreach (var file in new[] { "ThemeTokens", "Styles", "ComponentStyles", "HomeStyles" })
        {
            var styles = (Styles)AvaloniaXamlLoader.Load(new Uri($"avares://MacExplorer/Assets/{file}.axaml"));
            Application.Current.Styles.Add(styles);
            sharedStyles.Add(styles);
        }
        window.Closed += (_, _) => { foreach (var styles in sharedStyles) Application.Current.Styles.Remove(styles); };
        foreach (var control in new[] { content }.Concat(content.GetLogicalDescendants().OfType<Control>()))
        {
            if (control is TextBox or CheckBox or ComboBox)
                control.Theme = (ControlTheme)Application.Current!.FindResource(control.GetType())!;
        }
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileListSearchSurfaceStaysUnchangedThroughHoverPressAndFocus(bool dark)
    {
        var files = new FakeFileService("/tmp/search-surface-tests");
        var navigation = new NavigationViewModel(files) { IsHomePage = false };
        using var vm = CreateViewModel(files, navigation: navigation);
        using var tab = new ExplorerTabViewModel(vm);
        using var workspace = new ExplorerWorkspaceView { DataContext = tab };
        var window = InputAppearanceTests.CreateWindow(workspace, dark, 1280);
        try
        {
            var input = workspace.FindControl<TextBox>("SearchBox")!;
            var surface = input.GetVisualAncestors().OfType<Border>().Single(x => x.Classes.Contains("workspace-search"));
            var border = input.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_BorderElement");
            var resting = surface.Background;
            var breadcrumb = Assert.IsType<Border>(workspace.FindControl<BreadcrumbBar>("BreadcrumbControl")!.Content);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)resting!).Color);
            Assert.Equal(new Thickness(1), surface.BorderThickness);
            Assert.Equal(surface.Background, breadcrumb.Background);
            Assert.Equal(surface.BorderThickness, breadcrumb.BorderThickness);
            Assert.Equal(surface.BorderBrush, breadcrumb.BorderBrush);
            var point = input.TranslatePoint(new Point(20, 15), window)!.Value;
            window.MouseMove(point);
            Assert.Equal(resting, surface.Background);
            window.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(resting, surface.Background);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)border.Background!).Color);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(resting, surface.Background);
            window.MouseMove(new Point(1200, 590));
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.IsFocused);
            Assert.Equal(resting, surface.Background);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)border.Background!).Color);
        }
        finally { window.Close(); }
    }
}
