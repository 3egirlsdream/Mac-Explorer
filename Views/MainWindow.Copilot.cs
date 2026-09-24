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
    private bool _copilotClosed;
    private CancellationTokenSource? _copilotRunCts;
    private Task? _copilotRunTask;
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
        IReadOnlyList<CopilotSessionSummary> sessions;
        try
        {
            sessions = App.Services.GetRequiredService<CopilotStore>().ListSessions();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            // History is optional: preserve the existing rows and keep approval recovery usable.
            System.Diagnostics.Debug.WriteLine($"Copilot history refresh failed: {ex}");
            return;
        }
        CopilotHistoryList.Children.Clear();
        foreach (var session in sessions)
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
                try { Copilot.SwitchSession(session.Id); }
                catch (InvalidOperationException ex)
                {
                    AddCopilotLine("错误", ex.Message);
                    return;
                }
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
                {
                    try { App.Services.GetRequiredService<CopilotStore>().DeleteSession(session.Id); }
                    catch (InvalidOperationException ex) { AddCopilotLine("错误", ex.Message); }
                }
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
        try
        {
            if (Copilot.PrepareForSend() is { } notice)
            {
                CopilotTranscript.Children.Clear();
                AddCopilotLine("Copilot", notice);
                RefreshCopilotHistory();
            }
        }
        catch (Exception ex)
        {
            AddCopilotLine("错误", ex.Message);
            return;
        }
        var attachments = _copilotAttachments.ToArray();
        CopilotInput.Text = string.Empty;
        AddCopilotLine("你", message);
        AddCopilotAttachmentTags(attachments);
        ClearCopilotAttachments();
        RefreshCopilotHistory();
        _copilotRunTask = RunCopilotAsync((onUpdate, token) => Copilot.SendAsync(message, attachments, onUpdate, token), true);
        await _copilotRunTask;
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
        ScrollCopilotToEndWhenIdle();
    }

    private void CopilotInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        SendCopilotMessage(sender, e);
    }

    private async void ApproveCopilotPlan(object? sender, RoutedEventArgs e)
    {
        if (_copilotBusy || !Copilot.NeedsApproval) return;
        try { Copilot.ValidatePendingApprovalRecipient(); }
        catch (Exception ex)
        {
            if (!Copilot.NeedsApproval)
            {
                CopilotApprovalCard.IsVisible = false;
                CopilotTranscript.Children.Clear();
            }
            AddCopilotLine("错误", ex.Message);
            RefreshCopilotHistory();
            return;
        }
        RecordCopilotDecision(true);
        CopilotApprovalCard.IsVisible = false;
        _copilotRunTask = RunCopilotAsync((onUpdate, token) => Copilot.RespondToApprovalAsync(true, onUpdate, token), false);
        await _copilotRunTask;
    }

    private async void RejectCopilotPlan(object? sender, RoutedEventArgs e)
    {
        if (_copilotBusy || !Copilot.NeedsApproval) return;
        RecordCopilotDecision(false);
        CopilotApprovalCard.IsVisible = false;
        _copilotRunTask = RunCopilotAsync((onUpdate, token) => Copilot.RespondToApprovalAsync(false, onUpdate, token), false);
        await _copilotRunTask;
    }

    private void RecordCopilotDecision(bool approved)
    {
        var description = CopilotApprovalText.Text ?? string.Empty;
        AddCopilotDecisionCard(description, approved);
        TryRecordCopilotTurn(approved ? "approval-approved" : "approval-rejected", description);
    }

    private void TryRecordCopilotTurn(string role, string text)
    {
        try
        {
            App.Services.GetRequiredService<CopilotStore>().AddTurn(Copilot.SessionId, role, text);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            // Transcript persistence is not the approval gate. Keep a displayed error or a user's
            // rejection usable when the local database is unavailable; do not throw from async-void UI events.
            System.Diagnostics.Debug.WriteLine($"Copilot transcript persistence failed: {ex}");
        }
    }

    private void StopCopilotResponse(object? sender, RoutedEventArgs e)
    {
        if (_copilotBusy && CopilotStopButton.IsEnabled) _copilotRunCts?.Cancel();
    }

    private async Task CloseCopilotAsync()
    {
        _copilotClosed = true;
        if (_copilotRunTask != null && !_copilotRunTask.IsCompleted)
        {
            if (CopilotStopButton.IsEnabled) _copilotRunCts?.Cancel();
            await _copilotRunTask;
        }
        _copilot?.Dispose();
        _copilot = null;
    }

    private async Task RunCopilotAsync(Func<Action<AgentResponseUpdate>, CancellationToken, Task<CopilotReply>> action,
        bool allowStop)
    {
        var completedBeforeRequest = Copilot.CompletedOperationCount;
        using var runCts = new CancellationTokenSource();
        _copilotRunCts = runCts;
        _copilotBusy = true;
        CopilotSendButton.IsEnabled = false;
        CopilotStopButton.IsVisible = true;
        CopilotStopButton.IsEnabled = allowStop;
        ToolTip.SetTip(CopilotStopButton, allowStop ? "停止生成" : "已确认的文件操作将完成并返回实际结果");
        var trace = new CopilotRunTrace();
        var traceGate = new object();
        var rendered = new Dictionary<CopilotTraceStep, CopilotStepView>();
        var streamDirty = true;
        void RenderStream()
        {
            if (_copilotClosed) return;
            var followTail = CopilotTranscriptScroll.Extent.Height
                - CopilotTranscriptScroll.Viewport.Height - CopilotTranscriptScroll.Offset.Y <= 24;
            lock (traceGate)
            {
                if (!streamDirty) return;
                streamDirty = false;
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
            if (followTail)
            {
                var previousOffset = CopilotTranscriptScroll.Offset.Y;
                // Let layout measure the new text first; otherwise scrolling uses the old extent.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_copilotClosed && CopilotTranscriptScroll.Offset.Y >= previousOffset)
                        CopilotTranscriptScroll.ScrollToEnd();
                }, DispatcherPriority.Background);
            }
        }
        RenderStream();
        var streamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        streamTimer.Tick += (_, _) => RenderStream();
        streamTimer.Start();
        try
        {
            await action(update =>
            {
                lock (traceGate)
                {
                    trace.Apply(update);
                    streamDirty = true;
                }
            }, runCts.Token);
            lock (traceGate)
            {
                trace.Finish();
                streamDirty = true;
            }
            RenderStream();
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            lock (traceGate)
            {
                trace.Finish(failed: true);
                streamDirty = true;
            }
            RenderStream();
            try
            {
                Copilot.AbandonInterruptedRun();
                if (!_copilotClosed) AddCopilotLine("Copilot", "已停止生成。已开启新对话；旧记录仍保留。");
            }
            catch (Exception ex)
            {
                if (!_copilotClosed) AddCopilotLine("错误", $"已停止生成，但无法开启新对话：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            lock (traceGate)
            {
                trace.Finish(failed: true);
                streamDirty = true;
            }
            RenderStream();
            var completedMessage = Copilot.LastCompletedOperationMessage;
            var error = Copilot.CompletedOperationCount > completedBeforeRequest
                ? $"{completedMessage}\n上述工具已成功返回，但后续处理失败：{ex.Message}"
                : ex.Message;
            if (!_copilotClosed) AddCopilotLine("错误", error);
            TryRecordCopilotTurn("error", error);
        }
        finally
        {
            streamTimer.Stop();
            _copilotBusy = false;
            _copilotRunCts = null;
            if (!_copilotClosed)
            {
                CopilotStopButton.IsVisible = false;
                // FinishAsync may already have received approval requests before persistence fails.
                // Keep reject/approve reachable instead of leaving SendAsync permanently blocked.
                SynchronizeCopilotApproval();
                CopilotInput.IsEnabled = !CopilotApprovalCard.IsVisible;
                CopilotSendButton.IsEnabled = !CopilotApprovalCard.IsVisible;
                RefreshCopilotHistory();
            }
        }
    }

    private void SynchronizeCopilotApproval()
    {
        CopilotApprovalCard.IsVisible = Copilot.NeedsApproval;
        if (!Copilot.NeedsApproval) return;
        var plan = Copilot.PendingApprovalPlan;
        var destination = plan?.Capability.Impact == CapabilityImpact.DiscloseContent
            ? $"\n发送至：{Copilot.PendingRecipient}"
            : string.Empty;
        CopilotApprovalText.Text = plan == null
            ? "Copilot 请求执行未识别的操作。请拒绝并重新预览。"
            : $"{plan.Summary}\n影响：{string.Join("、", plan.AffectedPaths)}{destination}\n{plan.Capability.Confirmation}";
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

    private void ScrollCopilotToEndWhenIdle()
    {
        if (!_copilotBusy) CopilotTranscriptScroll.ScrollToEnd();
    }

    private CopilotStepView AddCopilotProcess(CopilotTraceStep step)
    {
        var card = new CopilotThinkingCard();
        card.Update(step);
        CopilotTranscript.Children.Add(card);
        ScrollCopilotToEndWhenIdle();
        return new CopilotStepView(card.Update);
    }

    private CopilotMarkdownView? AddCopilotLine(string source, string text)
    {
        var isUser = source == "你";
        var message = new StackPanel
        {
            Spacing = source == "Copilot" || isUser ? 0 : 4,
            MaxWidth = isUser ? 340 : double.PositiveInfinity,
            HorizontalAlignment = isUser
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Stretch
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
        ScrollCopilotToEndWhenIdle();
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
        ScrollCopilotToEndWhenIdle();
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
        ScrollCopilotToEndWhenIdle();
    }
}
