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

public sealed class CopilotToolGroupCardTests
{
    [AvaloniaFact]
    public void LegacyExecutionRecordsRenderAsCompactLabelAndDetailRows()
    {
        var card = new CopilotToolGroupCard
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        var window = new Window { Width = 350, Height = 300, Content = card };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show();
            card.Update(new CopilotTraceStep
            {
                Kind = "tools", Finished = true,
                Calls =
                [
                    new() { Name = "执行记录", Detail = "搜索可用能力：文件搜索 · 1 项", Finished = true },
                    new() { Name = "执行记录", Detail = "搜索可用能力：全部 · 81 项", Finished = true },
                    new() { Name = "执行记录", Detail = "查看能力说明：搜索文件", Finished = true },
                    new() { Name = "执行记录", Detail = "调用只读能力：搜索文件", Finished = true }
                ]
            });
            Assert.False(card.IsDetailsExpanded);
            var button = Assert.Single(card.GetVisualDescendants().OfType<Button>());
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            Assert.True(card.IsDetailsExpanded);
            Assert.DoesNotContain(card.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "执行记录");
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "文件搜索 · 1 项");
            Assert.True(card.Bounds.Height < 140, "Four records should fit in one compact group.");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BatchCollapsesAfterCompletionAndCanBeExpandedAgain()
    {
        var card = new CopilotToolGroupCard
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        var window = new Window { Width = 350, Height = 300, Content = card };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        var step = new CopilotTraceStep
        {
            Kind = "tools",
            Calls =
            [
                new() { CallId = "one", Name = "SearchCapabilities", Detail = "文件搜索" },
                new() { CallId = "two", Name = "CallReadOnly", Detail = "file.search" }
            ]
        };
        try
        {
            window.Show();
            card.Update(step);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.True(card.IsDetailsExpanded);
            var expandedHeight = card.Bounds.Height;
            var button = Assert.Single(card.GetVisualDescendants().OfType<Button>());
            Assert.True(button.Bounds.Width < card.Bounds.Width - 30,
                "Only the compact title control should receive hover feedback.");

            foreach (var call in step.Calls)
            {
                call.Result = "已完成";
                call.Finished = true;
            }
            step.Finished = true;
            card.Update(step);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.False(card.IsDetailsExpanded);
            Assert.True(card.Bounds.Height < expandedHeight);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(card.IsDetailsExpanded);
            Assert.Contains("收起明细", Avalonia.Automation.AutomationProperties.GetName(button));
        }
        finally { window.Close(); }
    }
}
