using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Window")]
public sealed class DialogSettingsAppearanceTests
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void FullHeightDialogKeepsSurfacesToTopAndChromeTransparent(bool dark)
    {
        var rail = new Border { Width = 220, BorderThickness = new Thickness(0, 0, 1, 0) };
        rail.Bind(Border.BackgroundProperty, rail.GetResourceObservable("TitleBarBackgroundBrush"));
        rail.Bind(Border.BorderBrushProperty, rail.GetResourceObservable("DialogDividerBrush"));
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        body.Children.Add(rail);
        var window = new DialogWindow { FullHeightContent = true, Content = body, Width = 820, Height = 620,
            Title = "Remote", RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        try
        {
            window.UpdateLayout();
            var frame = window.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "WindowFrame");
            Assert.Equal(Color.Parse(dark ? "#1B1D21" : "#FFFFFF"), ((ISolidColorBrush)frame.Background!).Color);
            Assert.Equal(new Point(0, 0), body.TranslatePoint(default, window));
            Assert.Equal(window.ClientSize.Height, rail.Bounds.Height);
            var title = window.GetVisualDescendants().OfType<WindowTitleBar>().Single();
            Assert.False(title.FindControl<TextBlock>("TitleText")!.IsVisible);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)title.FindControl<Grid>("TitleBarSurface")!.Background!).Color);
            Assert.True(title.FindControl<Button>("CloseButton")!.IsEnabled);
            Assert.False(title.FindControl<Button>("MinimizeButton")!.IsEnabled);
            Assert.False(title.FindControl<Button>("FullScreenButton")!.IsEnabled);
            window.IsModalInteractionBlocked = true;
            Assert.False(title.FindControl<Button>("CloseButton")!.IsEnabled);
            Assert.True(window.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "ModalTitleBarHitShield").IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void CompressionDialogUsesSharedContentAndGroupColors(bool dark)
    {
        var dialog = new CompressDialog { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        dialog.Show();
        try
        {
            var frame = dialog.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "WindowFrame");
            var group = dialog.GetVisualDescendants().OfType<Border>().First(x => x.Classes.Contains("settings-group"));
            Assert.Equal(Color.Parse(dark ? "#1B1D21" : "#FFFFFF"), ((ISolidColorBrush)frame.Background!).Color);
            Assert.Equal(Color.Parse(dark ? "#292D34" : "#F8F8F8"), ((ISolidColorBrush)group.Background!).Color);
            var divider = dialog.GetVisualDescendants().OfType<Border>().First(x => x.Classes.Contains("settings-divider"));
            Assert.Equal(Color.Parse(dark ? "#343942" : "#EEEEEE"), ((ISolidColorBrush)divider.BorderBrush!).Color);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void SmallDialogReservesButtonsAndMainWindowKeepsTitleBar()
    {
        var body = new Border();
        var window = new DialogWindow { Content = body, Width = 440, Height = 230 };
        window.Show();
        try { Assert.Equal(44, body.TranslatePoint(default, window)!.Value.Y); }
        finally { window.Close(); }
        var main = new AppWindow { Content = new Border(), Width = 440, Height = 230, Title = "Main" };
        main.Show();
        try
        {
            Assert.False(main.ExtendContentToTop);
            Assert.True(main.GetVisualDescendants().OfType<WindowTitleBar>().Single().FindControl<TextBlock>("TitleText")!.IsVisible);
        }
        finally { main.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteDraftsSurviveSwitchingAndOnlySavePersists(bool dark)
    {
        using var fixture = new RemoteFixture();
        fixture.Service.SaveServer(new RemoteServerInfo { Id = "first", Host = "first.test" });
        fixture.Service.SaveServer(new RemoteServerInfo { Id = "second", Host = "second.test" });
        var dialog = new RemoteConnectionDialog { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Width = 700, Height = 500 };
        dialog.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var list = dialog.FindControl<ListBox>("SavedServersList")!;
            var host = dialog.FindControl<TextBox>("HostBox")!;
            Assert.Equal("first.test", host.Text);
            host.Text = "edited.test";
            dialog.FindControl<TextBox>("PortBox")!.Text = "invalid port draft";
            list.SelectedIndex = 1; list.SelectedIndex = 0;
            Assert.Equal("edited.test", host.Text);
            Assert.Equal("invalid port draft", dialog.FindControl<TextBox>("PortBox")!.Text);
            Assert.Equal("first.test", fixture.Service.GetSavedServers()[0].Host);
            Click(dialog, "AddServerButton"); host.Text = "new.test";
            list.SelectedIndex = 0; Click(dialog, "AddServerButton");
            Assert.Equal("new.test", host.Text);
            Assert.Equal(2, fixture.Service.GetSavedServers().Count);
            Click(dialog, "SaveButton");
            Assert.Equal(3, fixture.Service.GetSavedServers().Count);
            Assert.Equal("new.test", Assert.IsType<RemoteServerInfo>(list.SelectedItem).Host);
            Click(dialog, "DeleteButton");
            Assert.Equal(2, fixture.Service.GetSavedServers().Count);
            var rail = dialog.FindControl<Border>("SavedServersPanel")!;
            Assert.Equal(0, rail.TranslatePoint(default, dialog)!.Value.Y);
            Assert.Equal(500, rail.Bounds.Height);
            Assert.NotEqual(Colors.Transparent, ((ISolidColorBrush)rail.Background!).Color);
            foreach (var name in new[] { "SaveButton", "ConnectButton", "AddServerButton" })
            {
                var button = dialog.FindControl<Button>(name)!;
                var origin = button.TranslatePoint(default, dialog)!.Value;
                Assert.True(origin.Y + button.Bounds.Height <= dialog.ClientSize.Height);
                Assert.True(origin.X + button.Bounds.Width <= dialog.ClientSize.Width);
            }
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void EmptyRemoteListStaysVisibleAndDiscardedDraftDoesNotPersist()
    {
        using var fixture = new RemoteFixture();
        var dialog = new RemoteConnectionDialog(); dialog.Show(); Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.FindControl<Border>("SavedServersPanel")!.IsVisible);
        Assert.False(dialog.FindControl<Button>("DeleteButton")!.IsEnabled);
        dialog.FindControl<TextBox>("HostBox")!.Text = "unsaved.test";
        dialog.Close();
        Assert.Empty(fixture.Service.GetSavedServers());
        dialog = new RemoteConnectionDialog(); dialog.Show(); Dispatcher.UIThread.RunJobs();
        try { Assert.True(string.IsNullOrEmpty(dialog.FindControl<TextBox>("HostBox")!.Text)); }
        finally { dialog.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingConnectionBlocksEditingAndFailureOrCloseKeepsSavedState(bool close)
    {
        using var fixture = new RemoteFixture(pendingConnection: true);
        var pending = fixture.Pending!;
        var dialog = new RemoteConnectionDialog(); dialog.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            dialog.FindControl<TextBox>("HostBox")!.Text = "connection.test";
            Click(dialog, "ConnectButton");
            Assert.False(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
            Assert.False(dialog.FindControl<Border>("SavedServersPanel")!.IsEnabled);
            Assert.False(dialog.FindControl<StackPanel>("ServiceParametersHost")!.IsEnabled);
            Click(dialog, "ConnectButton");
            Assert.Equal(1, pending.Attempts);
            if (close)
            {
                dialog.Close();
                Assert.True(pending.Token.IsCancellationRequested);
            }
            else
            {
                pending.Completion.SetException(new InvalidOperationException("fixture connection failure"));
                Dispatcher.UIThread.RunJobs();
                Assert.True(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
                Assert.True(dialog.FindControl<Border>("SavedServersPanel")!.IsEnabled);
                Assert.Equal("connection.test", dialog.FindControl<TextBox>("HostBox")!.Text);
                Assert.Contains("fixture connection failure", dialog.FindControl<TextBlock>("ConnectionStatus")!.Text);
            }
            Assert.Empty(fixture.Service.GetSavedServers());
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void SuccessfulConnectionSavesAndClosesWithExistingResultContract()
    {
        using var fixture = new RemoteFixture(pendingConnection: true);
        var dialog = new RemoteConnectionDialog(); dialog.Show(); Dispatcher.UIThread.RunJobs();
        using var client = new Renci.SshNet.SftpClient("fixture.test", "fixture", "fixture");
        try
        {
            dialog.FindControl<TextBox>("HostBox")!.Text = "fixture.test";
            Click(dialog, "ConnectButton");
            fixture.Pending!.Completion.SetResult(client);
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.IsVisible);
            Assert.True(dialog.Connected);
            var saved = Assert.Single(fixture.Service.GetSavedServers());
            Assert.Equal(dialog.Result!.Id, saved.Id);
            Assert.Equal(RemoteServiceType.Sftp, saved.ServiceType);
        }
        finally { dialog.Close(); }
    }

    private sealed class PendingRemoteService(RemoteConnectionService inner) : IRemoteConnectionService
    {
        public TaskCompletionSource<Renci.SshNet.SftpClient> Completion { get; } = new();
        public CancellationToken Token { get; private set; }
        public int Attempts { get; private set; }
        public Func<SftpHostKey, CancellationToken, Task<bool>>? ConfirmHostKeyAsync { get; set; }
        public Func<RemoteServerInfo, CancellationToken, Task<(string Passphrase, bool Remember)?>>? RequestPrivateKeyPassphraseAsync { get; set; }
        public string? CredentialLoadError => null;
        public SftpHostKey? GetTrustedHostKey(string host, int port) => null;
        public void ForgetHostKey(string host, int port) { }
        public void RetryCredentialMigration() { }
        public Task<Renci.SshNet.SftpClient> GetOrConnectAsync(RemoteServerInfo server, CancellationToken ct = default) => ConnectAsync(server, ct);
        public Task<Renci.SshNet.SftpClient> ConnectAsync(RemoteServerInfo server, CancellationToken ct = default)
        {
            Attempts++; Token = ct;
            ct.Register(() => Completion.TrySetCanceled(ct));
            return Completion.Task;
        }
        public void Disconnect(string id) { }
        public void DisconnectAll() { }
        public bool IsConnected(string id) => false;
        public Renci.SshNet.SftpClient? GetClient(string id) => null;
        public IReadOnlyList<RemoteServerInfo> GetSavedServers() => inner.GetSavedServers();
        public void SaveServer(RemoteServerInfo server) => inner.SaveServer(server);
        public void RemoveServer(string id) => inner.RemoveServer(id);
        public event EventHandler<string>? ConnectionLost { add { } remove { } }
        public event EventHandler<string>? Reconnecting { add { } remove { } }
        public event EventHandler<string>? ReconnectFailed { add { } remove { } }
    }

    private static void Click(Window dialog, string name) => dialog.FindControl<Button>(name)!
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private sealed class RemoteFixture : IDisposable
    {
        private readonly string _root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "dialog-" + Guid.NewGuid().ToString("N"));
        private readonly IServiceProvider _previous = App.Services;
        private readonly ServiceProvider _provider;
        public RemoteConnectionService Service { get; }
        public PendingRemoteService? Pending { get; }
        public RemoteFixture(bool pendingConnection = false)
        {
            Directory.CreateDirectory(_root);
            Service = new RemoteConnectionService(Path.Combine(_root, "servers.json"), new PrivacySecurityFollowupTests.MemoryCredentials());
            Pending = pendingConnection ? new PendingRemoteService(Service) : null;
            _provider = new ServiceCollection().AddSingleton<IRemoteConnectionService>((IRemoteConnectionService?)Pending ?? Service).BuildServiceProvider();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, _provider);
        }
        public void Dispose()
        {
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, _previous);
            _provider.Dispose(); Service.Dispose(); Directory.Delete(_root, true);
        }
    }
}
