using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Assets;
using MacExplorer.Copilot;

namespace MacExplorer.Views;

/// <summary>A compact disclosure for one assistant message's tool calls.</summary>
public sealed class CopilotToolGroupCard : Border
{
    private readonly TextBlock _title = new() { Classes = { "copilot-tool-title" } };
    private readonly PathIcon _chevron = new()
    {
        Data = Geometry.Parse(Icons.ChevronUp), Width = 13, Height = 13,
        [!PathIcon.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextMutedBrush")
    };
    private readonly StackPanel _details = new() { Spacing = 4 };
    private readonly Border _detailContainer;
    private readonly Button _button;
    private bool _expanded = true;
    private bool _autoCollapsed;
    private string? _lastSignature;

    public bool IsDetailsExpanded => _expanded;

    public CopilotToolGroupCard()
    {
        Classes.Add("copilot-tool-card");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new PathIcon
        {
            Data = Geometry.Parse(Icons.Code), Width = 14, Height = 14,
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
            Child = _details,
            Classes = { "copilot-tool-details" }
        };
        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(_button);
        content.Children.Add(_detailContainer);
        Child = content;
    }

    public void Update(CopilotTraceStep step)
    {
        var finished = step.Finished || step.Calls.Count > 0 && step.Calls.All(call => call.Finished);
        if (finished && !_autoCollapsed)
        {
            _autoCollapsed = true;
            _expanded = false;
        }
        _detailContainer.IsVisible = _expanded;
        _chevron.Data = Geometry.Parse(_expanded ? Icons.ChevronUp : Icons.ChevronDown);
        var failures = step.Calls.Count(call => call.Failed);
        var pending = step.Calls.Count(call => call.NeedsApproval && !call.Finished);
        _title.Text = $"工具调用 · {step.Calls.Count} 项 · "
            + (failures > 0 ? $"{failures} 项失败" : pending > 0 ? "待确认" : finished ? "已完成" : "执行中");
        AutomationProperties.SetName(_button, $"{_title.Text}，{(_expanded ? "收起" : "展开")}明细");

        var signature = JsonSerializer.Serialize(step.Calls);
        if (signature == _lastSignature) return;
        _lastSignature = signature;
        _details.Children.Clear();
        foreach (var call in step.Calls)
        {
            var (name, detail) = ToolLabel(call);
            var line = new StackPanel { Spacing = 1 };
            var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 5 };
            heading.Children.Add(new TextBlock
            {
                Text = name,
                Classes = { "copilot-tool-action" }
            });
            if (!string.IsNullOrWhiteSpace(detail))
            {
                var description = new TextBlock
                {
                    Text = detail, TextWrapping = TextWrapping.Wrap,
                    Classes = { "copilot-tool-detail" }
                };
                Grid.SetColumn(description, 1);
                heading.Children.Add(description);
            }
            line.Children.Add(heading);
            var result = call.NeedsApproval && !call.Finished ? "等待确认"
                : !call.Finished ? "正在调用…"
                : call.Result;
            if (!string.IsNullOrWhiteSpace(result))
            {
                var resultText = new TextBlock
                {
                    Text = result, TextWrapping = TextWrapping.Wrap,
                    Classes = { call.Failed ? "copilot-tool-failed" : "copilot-tool-detail" }
                };
                line.Children.Add(resultText);
            }
            _details.Children.Add(line);
        }
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        _detailContainer.IsVisible = _expanded;
        _chevron.Data = Geometry.Parse(_expanded ? Icons.ChevronUp : Icons.ChevronDown);
        AutomationProperties.SetName(_button, $"{_title.Text}，{(_expanded ? "收起" : "展开")}明细");
    }

    private static (string Name, string Detail) ToolLabel(CopilotToolCall call)
    {
        if (call.Name == "执行记录")
        {
            var separator = call.Detail.IndexOf('：');
            if (separator > 0)
                return (call.Detail[..separator], call.Detail[(separator + 1)..].Trim());
            return (call.Name, call.Detail);
        }
        var name = call.Name switch
        {
            "SearchCapabilities" => "搜索可用能力",
            "DescribeCapability" => "查看能力说明",
            "ReadArtifact" => "读取会话产物",
            "SaveClassificationProposal" => "保存分类方案",
            "CallReadOnly" => "调用只读能力",
            "PreviewOperation" => "预览操作",
            "ExecuteApprovedPlan" => "执行已批准操作",
            _ => call.Name
        };
        return (name, call.Detail);
    }
}
