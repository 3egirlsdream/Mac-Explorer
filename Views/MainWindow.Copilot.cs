using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Assets;
using MacExplorer.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class MainWindow
{
    private CopilotEngine? _copilot;
    private bool _copilotBusy;
    private readonly List<CopilotAttachment> _copilotAttachments = [];

    private CopilotEngine Copilot => _copilot ??= CreateCopilot();

    private CopilotEngine CreateCopilot()
    {
        var engine = new CopilotEngine(
            App.Services.GetRequiredService<IAppCapabilityRegistry>(),
            App.Services.GetRequiredService<CopilotSettings>(),
            App.Services.GetRequiredService<CopilotKeychain>(),
            App.Services.GetRequiredService<CopilotStore>(),
            App.Services.GetRequiredService<CopilotSkillCatalog>(),
            () => _activeFileList);
        return engine;
    }

    private void ToggleCopilot(object? sender, RoutedEventArgs e)
    {
        CopilotPanel.IsVisible = !CopilotPanel.IsVisible;
        if (!CopilotPanel.IsVisible) return;
        if (CopilotHistoryPane.IsVisible) RefreshCopilotHistory();
        ShowCopilotSession();
    }

    private void ShowCopilotSession()
    {
        if (CopilotTranscript.Children.Count == 0)
        {
            var legacyTools = new List<CopilotToolCall>();
            void FlushLegacyTools()
            {
                if (legacyTools.Count == 0) return;
                AddCopilotToolGroup(new CopilotTraceStep
                {
                    Kind = "tools", Calls = [..legacyTools], Finished = true
                });
                legacyTools.Clear();
            }
            foreach (var turn in Copilot.History)
            {
                if (turn.Role == "tool")
                {
                    legacyTools.Add(new CopilotToolCall
                    {
                        Name = "执行记录", Detail = turn.Text, Finished = true
                    });
                    continue;
                }
                FlushLegacyTools();
                switch (turn.Role)
                {
                    case "user": AddCopilotLine("你", turn.Text); break;
                    case "user-attachments": AddCopilotAttachmentTags(CopilotAttachments.Parse(turn.Text)); break;
                    case "thinking": AddCopilotProcess(new CopilotTraceStep
                    {
                        Kind = "thinking", Text = turn.Text, Finished = true
                    }); break;
                    case "assistant-step": AddCopilotLine("Copilot", turn.Text); break;
                    case "tool-batch":
                        if (JsonSerializer.Deserialize<CopilotTraceStep>(turn.Text) is { } batch)
                            AddCopilotToolGroup(batch);
                        break;
                    case "error": AddCopilotLine("错误", turn.Text); break;
                    case "approval-approved": AddCopilotDecisionCard(turn.Text, true); break;
                    case "approval-rejected": AddCopilotDecisionCard(turn.Text, false); break;
                    default: AddCopilotLine("Copilot", turn.Text); break;
                }
            }
            FlushLegacyTools();
            if (CopilotTranscript.Children.Count == 0)
                AddCopilotLine("Copilot", "你好。可以问我当前文件夹，或让我协助整理文件。修改文件前会显示计划供你确认。");
        }
        CopilotInput.Focus();
    }

    private void ToggleCopilotHistory(object? sender, RoutedEventArgs e)
    {
        CopilotHistoryPane.IsVisible = !CopilotHistoryPane.IsVisible;
        CopilotPanel.Width = CopilotHistoryPane.IsVisible ? 664 : 440;
        AutomationProperties.SetName(CopilotHistoryToggle,
            CopilotHistoryPane.IsVisible ? "收起历史会话" : "展开历史会话");
        ToolTip.SetTip(CopilotHistoryToggle,
            CopilotHistoryPane.IsVisible ? "收起历史会话" : "历史会话");
        if (CopilotHistoryPane.IsVisible) RefreshCopilotHistory();
    }

    private void RefreshCopilotHistory()
    {
        if (!CopilotHistoryPane.IsVisible) return;
        CopilotHistoryList.Children.Clear();
        foreach (var session in App.Services.GetRequiredService<CopilotStore>().ListSessions())
        {
            var row = new Border { Classes = { "copilot-history-row" } };
            row.Classes.Set("active", session.Id == Copilot.SessionId);
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var title = new Button
            {
                Content = new TextBlock
                {
                    Text = session.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Classes = { "ghost", "compact", "copilot-history-title" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(title, $"切换到会话：{session.Title}");
            ToolTip.SetTip(title, session.Title);
            title.Click += (_, _) =>
            {
                if (_copilotBusy || session.Id == Copilot.SessionId) return;
                Copilot.SwitchSession(session.Id);
                CopilotTranscript.Children.Clear();
                CopilotApprovalCard.IsVisible = false;
                CopilotInput.Text = string.Empty;
                ClearCopilotAttachments();
                CopilotInput.IsEnabled = true;
                CopilotSendButton.IsEnabled = true;
                ShowCopilotSession();
                RefreshCopilotHistory();
            };
            content.Children.Add(title);

            var more = new Button
            {
                Content = new PathIcon { Data = Geometry.Parse(Icons.MoreHorizontal), Width = 14, Height = 14 },
                Classes = { "ghost", "compact", "copilot-history-more" }
            };
            AutomationProperties.SetName(more, $"{session.Title} 的更多操作");
            ToolTip.SetTip(more, "更多操作");
            Grid.SetColumn(more, 1);
            var delete = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Children =
                    {
                        new PathIcon { Data = Geometry.Parse(Icons.Delete), Width = 14, Height = 14 },
                        new TextBlock { Text = "删除会话", VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                Classes = { "ghost", "compact" },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(delete, $"删除会话：{session.Title}");
            var popup = new Popup
            {
                Placement = PlacementMode.BottomEdgeAlignedRight,
                PlacementTarget = more,
                IsLightDismissEnabled = true,
                Child = new Border
                {
                    Classes = { "context-submenu-surface" },
                    Child = delete
                }
            };
            delete.Click += (_, _) =>
            {
                if (_copilotBusy) return;
                popup.IsOpen = false;
                if (session.Id == Copilot.SessionId)
                {
                    Copilot.DeleteCurrentSession();
                    CopilotTranscript.Children.Clear();
                    CopilotApprovalCard.IsVisible = false;
                    CopilotInput.Text = string.Empty;
                    ClearCopilotAttachments();
                    CopilotInput.IsEnabled = true;
                    CopilotSendButton.IsEnabled = true;
                    ShowCopilotSession();
                }
                else
                    App.Services.GetRequiredService<CopilotStore>().DeleteSession(session.Id);
                RefreshCopilotHistory();
            };
            more.Click += (_, _) =>
            {
                if (_copilotBusy) return;
                popup.IsOpen = !popup.IsOpen;
            };
            content.Children.Add(more);
            Grid.SetColumn(popup, 1);
            content.Children.Add(popup);
            row.Child = content;
            CopilotHistoryList.Children.Add(row);
        }
    }

    private void NewCopilotSession(object? sender, RoutedEventArgs e)
    {
        if (_copilotBusy) return;
        Copilot.NewSession();
        CopilotTranscript.Children.Clear();
        CopilotApprovalCard.IsVisible = false;
        CopilotInput.Text = string.Empty;
        ClearCopilotAttachments();
        CopilotInput.IsEnabled = true;
        CopilotSendButton.IsEnabled = true;
        AddCopilotLine("Copilot", "新对话已开始。");
        RefreshCopilotHistory();
    }

    private async void SendCopilotMessage(object? sender, RoutedEventArgs e)
    {
        var message = CopilotInput.Text?.Trim();
        if (_copilotBusy || CopilotApprovalCard.IsVisible
            || string.IsNullOrEmpty(message) && _copilotAttachments.Count == 0) return;
        message = string.IsNullOrEmpty(message) ? "请查看这些附件。" : message;
        var attachments = _copilotAttachments.ToArray();
        CopilotInput.Text = string.Empty;
        AddCopilotLine("你", message);
        AddCopilotAttachmentTags(attachments);
        ClearCopilotAttachments();
        RefreshCopilotHistory();
        await RunCopilotAsync(onUpdate => Copilot.SendAsync(message, attachments, onUpdate));
    }

    private void CopilotAttachmentDragOver(object? sender, DragEventArgs e)
    {
        var acceptsFiles = !_copilotBusy && !CopilotApprovalCard.IsVisible
            && e.DataTransfer.TryGetFiles()?.Any(item => item.Path.IsFile
                && Path.IsPathFullyQualified(item.Path.LocalPath)) == true;
        e.DragEffects = acceptsFiles ? DragDropEffects.Copy : DragDropEffects.None;
        CopilotAttachmentDropZone.Classes.Set("drag-over", acceptsFiles);
        e.Handled = true;
    }

    private void CopilotAttachmentDragLeave(object? sender, DragEventArgs e)
        => CopilotAttachmentDropZone.Classes.Set("drag-over", false);

    private void CopilotAttachmentDrop(object? sender, DragEventArgs e)
    {
        CopilotAttachmentDropZone.Classes.Set("drag-over", false);
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (_copilotBusy || CopilotApprovalCard.IsVisible) return;
        var incoming = CopilotAttachments.FromPaths(FileListView.GetDroppedPaths(e.DataTransfer));
        foreach (var attachment in incoming)
        {
            if (_copilotAttachments.Any(item => item.Path == attachment.Path)) continue;
            _copilotAttachments.Add(attachment);
        }
        if (incoming.Length == 0) return;
        e.DragEffects = DragDropEffects.Copy;
        RefreshCopilotAttachments();
    }

    private void ClearCopilotAttachments()
    {
        _copilotAttachments.Clear();
        RefreshCopilotAttachments();
    }

    private void RefreshCopilotAttachments()
    {
        CopilotAttachmentList.Children.Clear();
        foreach (var attachment in _copilotAttachments)
            CopilotAttachmentList.Children.Add(CreateCopilotAttachmentChip(attachment, removable: true));
        CopilotAttachmentEmptyHint.IsVisible = _copilotAttachments.Count == 0;
        CopilotAttachmentScroll.IsVisible = _copilotAttachments.Count > 0;
        CopilotAttachmentList.IsVisible = _copilotAttachments.Count > 0;
    }

    private Control CreateCopilotAttachmentChip(CopilotAttachment attachment, bool removable)
    {
        var name = string.IsNullOrEmpty(attachment.Name) ? attachment.Path : attachment.Name;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        content.Children.Add(new PathIcon
        {
            Data = Geometry.Parse(attachment.IsDirectory ? Icons.Folder : Icons.File),
            Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = name, MaxWidth = 100, TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 10, VerticalAlignment = VerticalAlignment.Center
        });
        var chip = new Border
        {
            Child = content,
            Classes = { "copilot-attachment-chip" },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = removable ? new Thickness(0, 6, 6, 0) : default
        };
        var layout = new Grid
        {
            Classes = { "copilot-attachment-tag" },
            Margin = new Thickness(2)
        };
        layout.Children.Add(chip);
        if (removable)
        {
            var remove = new Button
            {
                Content = new PathIcon
                {
                    Data = Geometry.Parse(Icons.Close), Width = 8, Height = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Classes = { "copilot-attachment-remove" },
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            AutomationProperties.SetName(remove, $"移除附件：{name}");
            ToolTip.SetTip(remove, $"移除附件：{name}");
            remove.Click += (_, _) =>
            {
                _copilotAttachments.RemoveAll(item => item.Path == attachment.Path);
                RefreshCopilotAttachments();
            };
            layout.Children.Add(remove);
        }
        AutomationProperties.SetName(layout, $"{(attachment.IsDirectory ? "文件夹" : "文件")}附件：{name}");
        ToolTip.SetTip(layout, attachment.Path);
        return layout;
    }

    private void AddCopilotAttachmentTags(IReadOnlyList<CopilotAttachment> attachments)
    {
        if (attachments.Count == 0) return;
        var items = new WrapPanel
        {
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, -6, 0, 0)
        };
        foreach (var attachment in attachments)
            items.Children.Add(CreateCopilotAttachmentChip(attachment, removable: false));
        AutomationProperties.SetName(items, $"本轮附件，共 {attachments.Count} 项");
        CopilotTranscript.Children.Add(items);
        CopilotTranscriptScroll.ScrollToEnd();
    }

    private void CopilotInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        SendCopilotMessage(sender, e);
    }

    private async void ApproveCopilotPlan(object? sender, RoutedEventArgs e)
    {
        if (_copilotBusy) return;
        RecordCopilotDecision(true);
        CopilotApprovalCard.IsVisible = false;
        await RunCopilotAsync(onUpdate => Copilot.RespondToApprovalAsync(true, onUpdate));
    }

    private async void RejectCopilotPlan(object? sender, RoutedEventArgs e)
    {
        if (_copilotBusy) return;
        RecordCopilotDecision(false);
        CopilotApprovalCard.IsVisible = false;
        await RunCopilotAsync(onUpdate => Copilot.RespondToApprovalAsync(false, onUpdate));
    }

    private void RecordCopilotDecision(bool approved)
    {
        var description = CopilotApprovalText.Text ?? string.Empty;
        AddCopilotDecisionCard(description, approved);
        App.Services.GetRequiredService<CopilotStore>().AddTurn(Copilot.SessionId,
            approved ? "approval-approved" : "approval-rejected", description);
    }

    private async Task RunCopilotAsync(Func<Action<AgentResponseUpdate>, Task<CopilotReply>> action)
    {
        var completedBeforeRequest = Copilot.CompletedOperationCount;
        _copilotBusy = true;
        CopilotSendButton.IsEnabled = false;
        var trace = new CopilotRunTrace();
        var traceGate = new object();
        var rendered = new Dictionary<CopilotTraceStep, CopilotStepView>();
        void RenderStream()
        {
            lock (traceGate)
            {
                foreach (var step in trace.Steps)
                {
                    if (!rendered.TryGetValue(step, out var view))
                    {
                        view = CreateCopilotStep(step);
                        rendered.Add(step, view);
                    }
                    view.Update(step);
                }
            }
            CopilotTranscriptScroll.ScrollToEnd();
        }
        RenderStream();
        var streamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        streamTimer.Tick += (_, _) => RenderStream();
        streamTimer.Start();
        try
        {
            var reply = await action(update =>
            {
                lock (traceGate) trace.Apply(update);
            });
            lock (traceGate) trace.Finish();
            RenderStream();
            if (reply.NeedsApproval)
            {
                var plan = reply.ApprovalPlan;
                var destination = plan?.Capability.Impact == CapabilityImpact.DiscloseContent
                    ? $"\n发送至：{App.Services.GetRequiredService<CopilotSettings>().Endpoint} / {App.Services.GetRequiredService<CopilotSettings>().Model}"
                    : string.Empty;
                CopilotApprovalText.Text = plan == null
                    ? "Copilot 请求执行未识别的操作。请拒绝并重新预览。"
                    : $"{plan.Summary}\n影响：{string.Join("、", plan.AffectedPaths)}{destination}\n{plan.Capability.Confirmation}";
                CopilotApprovalCard.IsVisible = true;
                CopilotApproveButton.IsEnabled = plan != null;
                var highRisk = plan?.Capability.Impact is CapabilityImpact.Destructive
                    or CapabilityImpact.RunScript or CapabilityImpact.InstallSoftware;
                CopilotApproveButton.Classes.Set("primary", !highRisk);
                CopilotApproveButton.Classes.Set("danger", highRisk);
                CopilotApproveButton.Content = plan?.Capability.Impact switch
                {
                    CapabilityImpact.DiscloseContent => "允许发送",
                    CapabilityImpact.Destructive => "确认删除",
                    CapabilityImpact.RunScript => "确认运行脚本",
                    CapabilityImpact.InstallSoftware => "确认安装",
                    _ => "确认执行"
                };
            }
        }
        catch (Exception ex)
        {
            lock (traceGate) trace.Finish(failed: true);
            RenderStream();
            var completedMessage = Copilot.LastCompletedOperationMessage;
            var error = Copilot.CompletedOperationCount > completedBeforeRequest
                ? completedMessage?.StartsWith("已提取", StringComparison.Ordinal) == true
                    ? $"{completedMessage}\n模型请求失败，未生成摘要：{ex.Message}"
                    : $"{completedMessage}\n操作已完成，但模型生成回复失败：{ex.Message}"
                : ex.Message;
            AddCopilotLine("错误", error);
            App.Services.GetRequiredService<CopilotStore>().AddTurn(Copilot.SessionId, "error", error);
        }
        finally
        {
            streamTimer.Stop();
            _copilotBusy = false;
            CopilotInput.IsEnabled = !CopilotApprovalCard.IsVisible;
            CopilotSendButton.IsEnabled = !CopilotApprovalCard.IsVisible;
            RefreshCopilotHistory();
        }
    }

    private CopilotStepView AddCopilotProcess(CopilotTraceStep step)
    {
        var card = new CopilotThinkingCard();
        card.Update(step);
        CopilotTranscript.Children.Add(card);
        CopilotTranscriptScroll.ScrollToEnd();
        return new CopilotStepView(card.Update);
    }

    private CopilotMarkdownView? AddCopilotLine(string source, string text)
    {
        var isUser = source == "你";
        var message = new StackPanel
        {
            Spacing = source == "Copilot" || isUser ? 0 : 4,
            MaxWidth = 340,
            HorizontalAlignment = isUser
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Left
        };
        if (source != "Copilot" && !isUser)
            message.Children.Add(new TextBlock
            {
                Text = source,
                HorizontalAlignment = isUser
                    ? Avalonia.Layout.HorizontalAlignment.Right
                    : Avalonia.Layout.HorizontalAlignment.Left,
                Classes = { source == "错误" ? "copilot-error-title" : "copilot-process-title" }
            });
        CopilotMarkdownView? markdown = null;
        Avalonia.Controls.Control content;
        if (source == "Copilot")
        {
            markdown = new CopilotMarkdownView();
            markdown.SetMarkdown(text);
            content = markdown;
        }
        else
            content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        message.Children.Add(new Border
        {
            Child = content,
            Classes = { isUser ? "copilot-user-bubble" : "copilot-assistant-bubble" }
        });
        CopilotTranscript.Children.Add(message);
        CopilotTranscriptScroll.ScrollToEnd();
        return markdown;
    }

    private CopilotStepView CreateCopilotStep(CopilotTraceStep step)
    {
        if (step.Kind == "thinking")
            return AddCopilotProcess(step);
        if (step.Kind == "assistant")
        {
            var markdown = AddCopilotLine("Copilot", step.Text);
            var lastText = step.Text;
            return new CopilotStepView(value =>
            {
                if (value.Text == lastText) return;
                markdown?.SetMarkdown(value.Text);
                lastText = value.Text;
            });
        }
        var group = AddCopilotToolGroup(step);
        return new CopilotStepView(group.Update);
    }

    private sealed record CopilotStepView(Action<CopilotTraceStep> Update);

    private CopilotToolGroupCard AddCopilotToolGroup(CopilotTraceStep step)
    {
        var view = new CopilotToolGroupCard();
        view.Update(step);
        CopilotTranscript.Children.Add(view);
        CopilotTranscriptScroll.ScrollToEnd();
        return view;
    }

    private void AddCopilotDecisionCard(string description, bool approved)
    {
        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(new TextBlock
        {
            Text = approved ? "已确认操作" : "已拒绝操作",
            Classes = { "copilot-approval-title" }
        });
        content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        CopilotTranscript.Children.Add(new Border { Child = content, Classes = { "copilot-approval-card" } });
        CopilotTranscriptScroll.ScrollToEnd();
    }
}
