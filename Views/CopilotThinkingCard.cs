using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Assets;
using MacExplorer.Copilot;

namespace MacExplorer.Views;

/// <summary>A compact disclosure for one model thinking step.</summary>
public sealed class CopilotThinkingCard : Border
{
    private readonly TextBlock _title = new() { Classes = { "copilot-tool-title" } };
    private readonly TextBlock _detail = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Classes = { "copilot-process-detail" }
    };
    private readonly PathIcon _chevron = new()
    {
        Data = Geometry.Parse(Icons.ChevronUp), Width = 13, Height = 13,
        [!PathIcon.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextMutedBrush")
    };
    private readonly Border _detailContainer;
    private readonly Button _button;
    private bool _expanded = true;
    private bool _autoCollapsed;

    public bool IsDetailsExpanded => _expanded;

    public CopilotThinkingCard()
    {
        Classes.Add("copilot-thinking-card");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new PathIcon
        {
            Data = Geometry.Parse(Icons.Brain), Width = 14, Height = 14,
            [!PathIcon.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextSecondaryBrush")
        });
        header.Children.Add(_title);
        header.Children.Add(_chevron);
        _button = new Button
        {
            Content = header,
            HorizontalAlignment = HorizontalAlignment.Left,
            Classes = { "ghost", "compact", "copilot-tool-toggle" }
        };
        _button.Click += (_, _) => Toggle();
        _detailContainer = new Border
        {
            Child = _detail,
            Classes = { "copilot-tool-details" }
        };
        var content = new StackPanel();
        content.Children.Add(_button);
        content.Children.Add(_detailContainer);
        Child = content;
    }

    public void Update(CopilotTraceStep step)
    {
        if (step.Finished && !_autoCollapsed)
        {
            _autoCollapsed = true;
            _expanded = false;
        }
        _title.Text = step.Finished
            ? step.Text == "处理未完成" ? "思考过程 · 已中断" : "思考过程 · 已完成"
            : "思考过程 · 进行中";
        _detail.Text = step.Text is "思考完成" or "处理未完成"
            ? "本次未返回思考内容"
            : string.IsNullOrWhiteSpace(step.Text) ? "正在思考…" : step.Text;
        UpdateDisclosure();
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        UpdateDisclosure();
    }

    private void UpdateDisclosure()
    {
        _detailContainer.IsVisible = _expanded;
        _chevron.Data = Geometry.Parse(_expanded ? Icons.ChevronUp : Icons.ChevronDown);
        AutomationProperties.SetName(_button, $"{_title.Text}，{(_expanded ? "收起" : "展开")}明细");
    }
}
