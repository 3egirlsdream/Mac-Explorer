using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FeatureUpgrade")]
[Trait("Area", "Window")]
public sealed class FileConflictDialogTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData("zh-CN", false)]
    [InlineData("zh-CN", true)]
    [InlineData("zh-Hant", false)]
    [InlineData("zh-Hant", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("fr", false)]
    [InlineData("fr", true)]
    public void ActionsUseOneRowWheneverTheyFitAndRecoverAfterNarrowing(string locale, bool dark)
    {
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.English);
        language.SetLanguage(locale);
        var entry = new FileSystemEntry { Name = "图片 2026-11-10 13.52.05.png",
            FullPath = Path.Combine(RuntimePaths.TestRoot!, "example.png") };
        var dialog = new FileConflictDialog(new(entry, entry, false, false))
        {
            Height = 600, SizeToContent = SizeToContent.Manual,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            dialog.Show();
            var root = Assert.IsType<Grid>(dialog.Content);
            var footer = Assert.IsType<Grid>(root.Children[1]);
            var cancel = Assert.Single(footer.Children.OfType<AppButton>());
            var actions = Assert.Single(footer.Children.OfType<WrapPanel>());
            foreach (var width in new[] { 900d, 616, 487, 440, 400, 900 })
            {
                dialog.Width = width;
                Settle(dialog);
                var buttons = actions.Children.Prepend(cancel).ToArray();
                var bounds = buttons.Select(button =>
                    new Rect(button.TranslatePoint(default, dialog)!.Value, button.Bounds.Size)).ToArray();
                foreach (var rect in bounds)
                {
                    Assert.InRange(rect.Left, 16, dialog.Bounds.Width - 16 - rect.Width + 0.01);
                    Assert.InRange(rect.Top, 44, dialog.Bounds.Height - rect.Height);
                    Assert.Equal(32, rect.Height);
                }
                for (var i = 0; i < bounds.Length; i++)
                for (var j = i + 1; j < bounds.Length; j++)
                    Assert.False(bounds[i].Intersects(bounds[j]));

                if (width == 900 || locale.StartsWith("zh", StringComparison.Ordinal) && width >= 440
                    || locale == "en" && width == 616)
                    Assert.All(bounds, rect => Assert.Equal(bounds[0].Top, rect.Top));

                // Body details may stack independently while the actions still fit on one row.
                if (width == 487 && locale == "zh-CN")
                {
                    var body = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(root.Children[0]).Content);
                    var comparison = Assert.IsType<Grid>(Assert.IsType<Border>(body.Children[1]).Child);
                    Assert.True(comparison.Children[2].Bounds.Top > comparison.Children[0].Bounds.Bottom);
                }
            }
        }
        finally { dialog.Close(); }
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }
}
