using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Copilot;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task BatchRenameShortcutsPreserveInlineRenameAndRequireFileListFocus()
    {
        using var theme = new FastListTestTheme(); var files = new FakeFileService("/tmp/BatchRenameInput");
        using var pane = CreateViewModel(files);
        pane.Entries = new ObservableCollection<FileSystemEntry>(new[] {
            new FileSystemEntry { FullPath = "/tmp/BatchRenameInput/a.txt", Name = "a.txt" },
            new FileSystemEntry { FullPath = "/tmp/BatchRenameInput/b.txt", Name = "b.txt" } });
        var view = new FileListView { DataContext = pane }; var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 900, Height = 600, Content = view }; var opened = 0; pane.RequestBatchRename += () => opened++;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); pane.SetSelection(pane.Entries); list.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Assert.Equal(1, opened);
            Assert.Empty(view.FindControl<Canvas>("FastRenameOverlay")!.Children);
            pane.SetSelection(pane.Entries.Take(1));
            window.KeyPress(Key.R, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.R, "r"); Assert.Equal(2, opened);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Dispatcher.UIThread.RunJobs();
            var editor = Assert.IsType<TextBox>(Assert.Single(view.FindControl<Canvas>("FastRenameOverlay")!.Children));
            window.KeyPress(Key.R, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.R, "r"); Assert.Equal(2, opened);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            var actions = await pane.LoadCompleteFileContextMenuAsync(pane.Entries[0]);
            Assert.Single(actions, a => a.Label.StartsWith("重命名"));
            pane.SetSelection(pane.Entries);
            actions = await pane.LoadCompleteFileContextMenuAsync(pane.Entries[0]);
            var action = Assert.Single(actions, a => a.Label.StartsWith("重命名"));
            Assert.Equal("重命名 2 项…", action.Label); await action.Execute!(); Assert.Equal(3, opened);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BatchRenameToolbarTracksSelectionAndReadOnlyScope()
    {
        using var pane = CreateViewModel(new FakeFileService("/tmp/BatchRenameToolbar"));
        var toolbar = new FinderToolbar { DataContext = pane }; var button = toolbar.FindControl<Button>("BatchRenameButton")!;
        Assert.False(button.IsEnabled);
        var entry = new FileSystemEntry { FullPath = "/tmp/BatchRenameToolbar/a.txt", Name = "a.txt" };
        pane.Entries.Add(entry); pane.SelectEntry(entry); Dispatcher.UIThread.RunJobs(); Assert.True(button.IsEnabled);
        pane.ClearSelection(); Dispatcher.UIThread.RunJobs(); Assert.False(button.IsEnabled);
        using var readOnly = CreateViewModel(new FakeFileService("/tmp/BatchRenameReadOnly"), browseOnly: true);
        var readOnlyToolbar = new FinderToolbar { DataContext = readOnly };
        readOnly.Entries.Add(entry); readOnly.SelectEntry(entry); Dispatcher.UIThread.RunJobs();
        Assert.False(readOnlyToolbar.FindControl<Button>("BatchRenameButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task CopilotHandsEditableRulesAndFrozenCrossDirectorySelectionToWorkbench()
    {
        var files = new FakeFileService("/tmp/BatchRenameCopilot");
        var entries = new[] { new FileSystemEntry { FullPath = "/tmp/BatchRenameCopilot/a.txt", Name = "a.txt" },
            new FileSystemEntry { FullPath = "/tmp/OtherBatchRename/b.txt", Name = "b.txt" } };
        foreach (var entry in entries) files.Seed(entry);
        using var pane = CreateViewModel(files); pane.Entries = new ObservableCollection<FileSystemEntry>(entries); pane.SetSelection(entries);
        var id = pane.CaptureBatchRenameSelection(); pane.ClearSelection(); var opened = 0; pane.RequestBatchRename += () => opened++;
        var registry = BatchRegistry(files);
        var result = await registry.ExecuteUiAsync("ui.batch-rename-dialog", JsonSerializer.Serialize(new {
            selectionId = id, rules = new object[] { new { type = "Template", templateText = "项目_{n:000}" }, new { type = "CaseConversion", target = "Extension", caseMode = "Lowercase" } },
            options = new { sort = "Modified", descending = true, restartPerDirectory = true }
        }), pane);
        Assert.True(result.Success); Assert.Equal(1, opened);
        var request = pane.TakeBatchRenameRequest()!;
        Assert.Equal(entries.Select(e => e.FullPath), request.Entries.Select(e => e.FullPath)); Assert.Equal(2, request.Rules.Count);
        Assert.Equal(RenameSort.Modified, request.Options.Sort); Assert.True(request.Options.RestartPerDirectory);
        foreach (var entry in entries) Assert.NotNull(await files.GetEntryAsync(entry.FullPath));
        Assert.False((await registry.ExecuteUiAsync("ui.batch-rename-dialog", "{}", pane)).Success);
    }

    [AvaloniaFact]
    public async Task CopilotRuleArraySupportsCrossDirectoryExecutionAndRejectsAmbiguousRuleInputs()
    {
        var files = new FakeFileService("/tmp/BatchRenameCopilotArray");
        var paths = new[] { "/tmp/BatchRenameCopilotArray/a.txt", "/tmp/OtherBatchRenameArray/a.txt" };
        foreach (var path in paths) files.Seed(new FileSystemEntry { FullPath = path, Name = "a.txt" });
        using var pane = CreateViewModel(files); var rename = new BatchRenameService(files); var registry = BatchRegistry(files, rename);
        var arguments = JsonSerializer.Serialize(new { paths, rules = new[] { new { type = "Template", templateText = "项目_{n:000}" } } });
        var plan = await registry.PreviewAsync("file.batch-rename", arguments, pane);
        Assert.Contains("项目_001.txt", plan.Summary); Assert.Contains("项目_002.txt", plan.Summary);
        Assert.True((await registry.ExecuteApprovedAsync(plan.Id, pane)).Success);
        Assert.NotNull(await files.GetEntryAsync("/tmp/OtherBatchRenameArray/项目_002.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.ExecuteUiAsync("ui.batch-rename-dialog",
            "{\"paths\":[\"/tmp/OtherBatchRenameArray/项目_002.txt\"],\"rule\":{\"type\":\"AddPrefix\"},\"rules\":[{\"type\":\"Cleanup\"}]}", pane));
    }

    private static AppCapabilityRegistry BatchRegistry(IFileService files, IBatchRenameService? rename = null)
    {
        rename ??= new BatchRenameService(files);
        return new AppCapabilityRegistry(files, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, rename,
            new BatchRenameOperationService(rename, new FileOperationHistoryService(files, batchRename: rename), new BackgroundTaskManager(null)));
    }
}
