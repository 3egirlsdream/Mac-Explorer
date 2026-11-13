using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "RemoteEdit")]
public sealed class RemoteEditDialogTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData("zh-CN")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("fr")]
    public void ActionsKeepTheirNaturalWidthAndReturnToOneRowAfterResizing(string locale)
    {
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.English);
        language.SetLanguage(locale);
        var dialog = Create(new Edits(Session("example.txt", RemoteEditState.Failed)), 650, false);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Height = 650;
        try
        {
            dialog.Show();
            foreach (var width in new[] { 900d, 650, 420, 400, 900 })
            {
                dialog.Width = width; Settle(dialog);
                var buttons = new[] { "StopRemoteEdit", "ReconnectRemoteEdit", "RetryRemoteEdit" }
                    .Select(name => Find<AppButton>(dialog, name)).ToArray();
                var bounds = buttons.Select(button => new Rect(button.TranslatePoint(default, dialog)!.Value, button.Bounds.Size)).ToArray();
                foreach (var rect in bounds)
                {
                    Assert.InRange(rect.Left, 20, dialog.Bounds.Width - 20 - rect.Width + 0.01);
                    Assert.InRange(rect.Top, 44, dialog.Bounds.Height - rect.Height);
                }
                if (width == 900 || locale == "en" && width == 650 || locale.StartsWith("zh", StringComparison.Ordinal))
                    Assert.True(bounds.All(rect => rect.Top == bounds[0].Top),
                        $"Actions unexpectedly stacked at {locale}/{width}, actual {dialog.Bounds.Size}: {string.Join(", ", buttons.Select(button => $"{button.Content}: {button.DesiredSize}"))}");
            }
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("zh-CN", false, 650)]
    [InlineData("zh-Hant", true, 650)]
    [InlineData("en", false, 420)]
    [InlineData("fr", true, 420)]
    public void FailureDetailsAndActionsFitAcrossLanguagesAndNarrowWindows(string locale, bool dark, double width)
    {
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.English);
        language.SetLanguage(locale);
        var edits = new Edits(Session("example.txt", RemoteEditState.Failed) with
            { Error = "Connection refused", FailureReason = RemoteEditFailureReason.ConnectionRefused });
        var dialog = Create(edits, width, dark);
        try
        {
            dialog.Show(); Settle(dialog);
            Assert.Equal(LocalizationText.Get("remote-edit.heading.failed"), Find<TextBlock>(dialog, "RemoteEditStatusHeading").Text);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == LocalizationText.Get("remote-edit.error.ConnectionRefused"));
            Assert.Equal("Connection refused", Find<SelectableTextBlock>(dialog, "RemoteEditErrorDetail").Text);
            Assert.True(Find<AppButton>(dialog, "RetryRemoteEdit").IsDefault);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<AppButton>(), button => button.Name == "CloseRemoteEdit");
            foreach (var name in new[] { "StopRemoteEdit", "ReconnectRemoteEdit", "RetryRemoteEdit", "RevealRemoteEditCopy" })
            {
                var button = Find<AppButton>(dialog, name);
                var origin = button.TranslatePoint(default, dialog)!.Value;
                Assert.InRange(origin.X, 0, dialog.Bounds.Width - button.Bounds.Width);
                Assert.InRange(origin.Y, 44, dialog.Bounds.Height - button.Bounds.Height);
                Assert.Equal(32, button.Bounds.Height);
            }
            var title = dialog.GetVisualDescendants().OfType<WindowTitleBar>().Single();
            var divider = title.FindControl<Border>("TitleBarDivider")!;
            Assert.True(divider.IsVisible); Assert.Equal(1, divider.Bounds.Height); Assert.False(divider.IsHitTestVisible);
            Assert.Equal(Color.Parse(dark ? "#343942" : "#EEEEEE"), ((ISolidColorBrush)divider.Background!).Color);
        }
        finally { dialog.Close(); }
        Assert.Equal(0, edits.Subscribers);
    }

    [AvaloniaFact(Explicit = true)]
    public void RemovingTheLastSessionCollapsesTheEmptyFooter()
    {
        using var theme = new FastListTestTheme();
        var edits = new Edits(Session("example.txt", RemoteEditState.Failed));
        var dialog = Create(edits, 420, false);
        try
        {
            dialog.Show(); Settle(dialog);
            var root = Assert.IsType<Grid>(dialog.Content);
            var footer = root.Children[1];
            Assert.True(footer.IsVisible);
            edits.Set(); Settle(dialog);
            Assert.False(footer.IsVisible);
            Assert.Equal(0, root.RowSpacing);
            Assert.Equal(root.Bounds.Height, root.Children[0].Bounds.Bottom);
            dialog.GetVisualDescendants().OfType<WindowTitleBar>().Single()
                .FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(dialog.IsVisible);
        }
        finally { dialog.Close(); }
        Assert.Equal(0, edits.Subscribers);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SwitchingSessionsKeepsActionsAndRevealBoundToTheSelectedFile()
    {
        using var theme = new FastListTestTheme();
        var first = Session("first.txt", RemoteEditState.Conflict); var second = Session("second.txt", RemoteEditState.Failed);
        var edits = new Edits(first, second); string? revealed = null;
        var dialog = Create(edits, 650, false, path => { revealed = path; return Task.CompletedTask; });
        try
        {
            dialog.Show(); Settle(dialog);
            Assert.True(Find<AppButton>(dialog, "KeepRemoteEditLocal").IsDefault);
            Click(dialog, "RevealRemoteEditCopy"); Assert.Equal(first.LocalPath, revealed);
            Click(dialog, "KeepRemoteEditLocal");
            await Until(() => edits.Stopped == first.LocalPath); Settle(dialog);
            Assert.Null(edits.Resolved);
            var picker = Find<AppComboBox>(dialog, "RemoteEditSessionPicker"); picker.SelectedIndex = 1; Settle(dialog);
            Click(dialog, "RetryRemoteEdit"); Assert.Equal(second.LocalPath, edits.Retried);
            Click(dialog, "ReconnectRemoteEdit"); Assert.Equal(second.LocalPath, edits.Reconnected);
            Click(dialog, "RevealRemoteEditCopy"); Assert.Equal(second.LocalPath, revealed);
            edits.Set(second with { Error = "new error" }, first); Settle(dialog);
            picker = Find<AppComboBox>(dialog, "RemoteEditSessionPicker");
            Assert.Equal(second.LocalPath, Assert.IsType<ComboBoxItem>(picker.SelectedItem).Tag);
        }
        finally { dialog.Close(); }
        edits.Set(first); Dispatcher.UIThread.RunJobs(); Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task RetryingOneFileLeavesStopRevealAndOtherSessionsAvailable()
    {
        using var theme = new FastListTestTheme();
        var first = Session("busy.txt", RemoteEditState.Failed); var second = Session("other.txt", RemoteEditState.Failed);
        var edits = new Edits(first, second) { RetryBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        string? revealed = null;
        var dialog = Create(edits, 650, false, path => { revealed = path; return Task.CompletedTask; });
        try
        {
            dialog.Show(); Settle(dialog); Click(dialog, "RetryRemoteEdit");
            Assert.False(Find<AppButton>(dialog, "RetryRemoteEdit").IsEffectivelyEnabled);
            Assert.True(Find<AppButton>(dialog, "StopRemoteEdit").IsEffectivelyEnabled);
            Click(dialog, "RevealRemoteEditCopy"); Assert.Equal(first.LocalPath, revealed);
            var picker = Find<AppComboBox>(dialog, "RemoteEditSessionPicker");
            Assert.True(picker.IsEffectivelyEnabled); picker.SelectedIndex = 1; Settle(dialog);
            Assert.True(Find<AppButton>(dialog, "ReconnectRemoteEdit").IsEffectivelyEnabled);
            Click(dialog, "ReconnectRemoteEdit"); Assert.Equal(second.LocalPath, edits.Reconnected);
            picker = Find<AppComboBox>(dialog, "RemoteEditSessionPicker"); picker.SelectedIndex = 0; Settle(dialog);
            Click(dialog, "StopRemoteEdit"); Assert.Equal(first.LocalPath, edits.Stopped);
            edits.RetryBarrier.SetResult();
            await Until(() => Find<AppButton>(dialog, "RetryRemoteEdit").IsEffectivelyEnabled);
        }
        finally { edits.RetryBarrier.TrySetResult(); dialog.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ReplacingTheRemoteFileStillRequiresTheExistingConfirmation()
    {
        using var theme = new FastListTestTheme();
        var session = Session("conflict.txt", RemoteEditState.Conflict); var edits = new Edits(session);
        var dialog = Create(edits, 650, false);
        try
        {
            dialog.Show(); Settle(dialog);
            Click(dialog, "ReplaceRemoteEdit");
            await Until(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any());
            dialog.OwnedWindows.OfType<ConfirmDialog>().Single().Close(false);
            await Until(() => Find<AppButton>(dialog, "ReplaceRemoteEdit").IsEffectivelyEnabled);
            Assert.Null(edits.Resolved);
            Click(dialog, "ReplaceRemoteEdit");
            await Until(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any());
            dialog.OwnedWindows.OfType<ConfirmDialog>().Single().Close(true);
            await Until(() => edits.Resolved != null); Assert.Equal(session.LocalPath, edits.Resolved);
        }
        finally { dialog.Close(); }
    }

    private static RemoteEditSession Session(string name, RemoteEditState state) => new(
        Path.Combine(RuntimePaths.TestRoot!, "remote-edit", "session-very-long-identifier", name), "/shared/projects/" + name, "test-server", state, null, true);
    private static RemoteEditDialog Create(Edits edits, double width, bool dark, Func<string, Task>? reveal = null)
    {
        var dialog = new RemoteEditDialog(edits, reveal ?? (_ => Task.CompletedTask)) { Width = width,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        dialog.Styles.Add(new StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml") });
        return dialog;
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) => Find<AppButton>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Settle(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static async Task Until(Func<bool> ready)
    {
        for (var i = 0; i < 100; i++) { Dispatcher.UIThread.RunJobs(); if (ready()) return; await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.Fail("Remote edit dialog did not settle.");
    }
    private sealed class Edits(params RemoteEditSession[] sessions) : IRemoteFileEditService
    {
        private Action? _changed;
        public int Subscribers { get; private set; }
        public IReadOnlyList<RemoteEditSession> Sessions { get; private set; } = sessions;
        public string? Retried, Reconnected, Stopped, Resolved;
        public TaskCompletionSource? RetryBarrier;
        public event Action? Changed { add { _changed += value; Subscribers++; } remove { _changed -= value; Subscribers--; } }
        public void Set(params RemoteEditSession[] values) { Sessions = values; _changed?.Invoke(); }
        public Task RetryAsync(string path) { Retried = path; return RetryBarrier?.Task ?? Task.CompletedTask; }
        public Task ReconnectAsync(string path) { Reconnected = path; return Task.CompletedTask; }
        public Task ResolveConflictAsync(string path, bool overwriteRemote) { Assert.True(overwriteRemote); Resolved = path; return Task.CompletedTask; }
        public void StopWatching(string path) { Stopped = path; Set(Sessions.Select(session => session.LocalPath == path ? session with { State = RemoteEditState.Stopped } : session).ToArray()); }
        public Task<string> DownloadForEditAsync(string remotePath, string serverId) => throw new NotSupportedException();
        public Task UploadAsync(string localPath, string remotePath, string serverId) => throw new NotSupportedException();
        public void WatchForChanges(string localTempPath, string remotePath, string serverId) { }
        public void CleanupTempFiles() { }
    }
}
