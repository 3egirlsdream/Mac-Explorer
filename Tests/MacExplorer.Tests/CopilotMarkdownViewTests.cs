using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class CopilotMarkdownViewTests
{
    [AvaloniaFact]
    public void AssistantBubbleRendersAndUpdatesMarkdownInPlace()
    {
        var view = new CopilotMarkdownView();
        var bubble = new Border { Child = view, Classes = { "copilot-assistant-bubble" } };
        var message = new StackPanel { MaxWidth = 340 };
        message.Children.Add(bubble);
        var window = new Window { Width = 317, Height = 240, Content = message };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show();
            view.SetMarkdown("**正在生成");
            Dispatcher.UIThread.RunJobs();
            var renderer = Assert.Single(window.GetVisualDescendants().OfType<Control>()
                .Where(control => control.Name == "Viewer"));
            var markdown = renderer.GetType().GetProperty("Markdown")!;
            Assert.Equal("**正在生成", markdown.GetValue(renderer));

            view.SetMarkdown("你好。可以问我当前文件夹，或让我协助整理文件。修改文件前会显示计划供你确认。");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.True(bubble.Bounds.Height < 80,
                $"Two-line Markdown bubble was {bubble.Bounds.Height} high.");

            view.SetMarkdown("**正在生成**\n\n- 第一项\n- 第二项");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(renderer, Assert.Single(window.GetVisualDescendants().OfType<Control>()
                .Where(control => control.Name == "Viewer")));
            Assert.Equal("**正在生成**\n\n- 第一项\n- 第二项", markdown.GetValue(renderer));
        }
        finally { window.Close(); }
    }
}
