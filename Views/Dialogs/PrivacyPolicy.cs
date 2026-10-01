using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;

namespace MacExplorer.Views.Dialogs;

internal static class PrivacyPolicy
{
    private static string? ConfiguredUrl => typeof(PrivacyPolicy).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(value => value.Key == "PrivacyPolicyUrl")?.Value;
    internal static Uri? PublishedUri => Uri.TryCreate(ConfiguredUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    internal static string Status => PublishedUri == null ? "查看随包隐私政策。" : "查看隐私政策。";

    internal static async Task ShowAsync(Window owner)
    {
        if (PublishedUri is { } uri)
        {
            if (!await owner.Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException("无法打开隐私政策，请稍后重试。");
            return;
        }
        using var stream = typeof(PrivacyPolicy).Assembly.GetManifestResourceStream("MacExplorer.PrivacyPolicy.txt")!;
        using var reader = new StreamReader(stream);
        var close = new Button { Content = "关闭", Classes = { "secondary", "compact" }, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new DialogWindow { Title = "隐私政策", Width = 560, Height = 520,
            MinWidth = 360, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        dialog.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        close.Click += (_, _) => dialog.Close();
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        grid.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = await reader.ReadToEndAsync(), TextWrapping = TextWrapping.Wrap,
                Classes = { "settings-description" }, Margin = new Thickness(0, 0, 12, 0) } });
        Grid.SetRow(close, 1); grid.Children.Add(close); dialog.Content = grid;
        await dialog.ShowDialog(owner);
    }
}
