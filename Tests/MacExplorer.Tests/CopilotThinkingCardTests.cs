using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Copilot;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class CopilotThinkingCardTests
{
    [AvaloniaFact]
    public void ThinkingExpandsWhileStreamingAndCollapsesWhenFinished()
    {
        var card = new CopilotThinkingCard
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        var window = new Window { Width = 350, Height = 300, Content = card };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        var step = new CopilotTraceStep { Kind = "thinking" };
        try
        {
            window.Show();
            card.Update(step);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.True(card.IsDetailsExpanded);
            var button = Assert.Single(card.GetVisualDescendants().OfType<Button>());
            Assert.True(button.Bounds.Width < card.Bounds.Width - 30);
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "正在思考…");

            step.Text = "先查看目录，再搜索文件。";
            card.Update(step);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == step.Text);
            var expandedHeight = card.Bounds.Height;

            step.Finished = true;
            card.Update(step);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.False(card.IsDetailsExpanded);
            Assert.True(card.Bounds.Height < expandedHeight);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(card.IsDetailsExpanded);
            Assert.Contains("收起明细", AutomationProperties.GetName(button));
            card.Update(step);
            Assert.True(card.IsDetailsExpanded);
        }
        finally { window.Close(); }
    }
}
