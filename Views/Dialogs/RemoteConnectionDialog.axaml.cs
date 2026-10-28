using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.DependencyInjection;
using MacExplorer.Services.Impl;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Views.Dialogs;

public partial class RemoteConnectionDialog : DialogWindow
{
    private readonly IRemoteConnectionService _connectionService;
    private readonly IServerConnectionCoordinator? _coordinator;
    private readonly RemoteServerInfo? _initialServer;
    private RemoteServiceType _activeType;
    private bool _loadingType;
    private readonly Dictionary<RemoteServiceType, ServerDraft> _protocolDrafts = new();
    private RemoteServerInfo? _editingServer;
    private bool _connecting;
    private sealed record ServerDraft(string Name, string Host, string Port, string Username,
        string Password, string KeyPath, string Passphrase, bool Remember, string Path, bool UseKey,
        RemoteServiceType Type, string Share, string Domain);

    private readonly CancellationTokenSource _connectionCancellation = new();
    public RemoteServerInfo? Result { get; private set; }
    public bool Connected { get; private set; }

    public RemoteConnectionDialog() : this(null) { }

    public RemoteConnectionDialog(RemoteServerInfo? initialServer)
    {
        InitializeComponent();
        KeyPathBox.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            KeyPassphraseBox.Text = "";
            RememberKeyPassphrase.IsChecked = false;
        };
        _connectionService = App.Services.GetRequiredService<IRemoteConnectionService>();
        _coordinator = App.Services.GetService<IServerConnectionCoordinator>();
        _initialServer = initialServer;
        Height = initialServer?.ServiceType == RemoteServiceType.WebDav ? 400 : initialServer?.ServiceType == RemoteServiceType.Smb ? 540 : 520;
        Loaded += OnLoaded;
        Closed += (_, _) => _connectionCancellation.Cancel();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _editingServer = _initialServer;
        if (_initialServer != null) LoadDraft(DraftForServer(_initialServer));
        else UpdateProtocolVisibility();
        if (_connectionService.CredentialLoadError is { } error) AppToast.ShowError(this, error);
        HostBox.Focus();
    }

    private ServerDraft CaptureDraft() => new(NameBox.Text ?? "", HostBox.Text ?? "", PortBox.Text ?? "", UsernameBox.Text ?? "",
        PasswordBox.Text ?? "", KeyPathBox.Text ?? "", KeyPassphraseBox.Text ?? "",
        RememberKeyPassphrase.IsChecked == true, DefaultPathBox.Text ?? "", KeyRadio.IsChecked == true,
        _activeType, ShareBox.Text ?? "", DomainBox.Text ?? "");

    private static ServerDraft DraftForServer(RemoteServerInfo server) => new(server.Name, server.ServiceType == RemoteServiceType.WebDav ? server.WebDavUrl : server.Host,
        server.Port.ToString(), server.Username, server.Password, server.PrivateKeyPath,
        server.PrivateKeyPassphrase, server.RememberPrivateKeyPassphrase, server.DefaultPath,
        server.AuthMethod == RemoteAuthMethod.PrivateKey, server.ServiceType, server.ShareName, server.Domain);

    private void LoadDraft(ServerDraft draft)
    {
        _loadingType = true;
        _activeType = draft.Type;
        ShareBox.Text = draft.Share;
        DomainBox.Text = draft.Domain;
        NameBox.Text = draft.Name;
        HostBox.Text = draft.Host;
        PortBox.Text = draft.Port;
        UsernameBox.Text = draft.Username;
        PasswordBox.Text = draft.Password;
        KeyPathBox.Text = draft.KeyPath;
        KeyPassphraseBox.Text = draft.Passphrase;
        RememberKeyPassphrase.IsChecked = draft.Remember;
        DefaultPathBox.Text = draft.Path;
        KeyRadio.IsChecked = draft.UseKey;
        PasswordRadio.IsChecked = !draft.UseKey;
        ServiceTypeBox.SelectedIndex = (int)draft.Type;
        _loadingType = false;
        UpdateProtocolVisibility();
        HostError.IsVisible = false;
        OnAuthMethodChanged(this, new RoutedEventArgs());
    }

    private void SetConnecting(bool value)
    {
        _connecting = value;
        ServiceParametersHost.IsEnabled = !value;
        NameBox.IsEnabled = !value;
        ServiceTypeBox.IsEnabled = !value;
        AuxiliaryActions.IsEnabled = !value;
        SystemConnectButton.IsEnabled = !value;
        ConnectButton.IsEnabled = !value;
        SaveButton.IsEnabled = !value;
        ConnectButton.Content = MacExplorer.Services.Impl.LocalizationText.Get(value ? "remote.connecting" : "ui.499");
    }

    private void OnAuthMethodChanged(object? sender, RoutedEventArgs e)
    {
        var mounted = _activeType != RemoteServiceType.Sftp;
        var isPassword = mounted || PasswordRadio.IsChecked == true;
        PasswordPanel.IsVisible = isPassword;
        KeyPanel.IsVisible = !isPassword;
        KeyPassphrasePanel.IsVisible = !isPassword;
    }

    private async void OnBrowseKeyFile(object? sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenAuthorizedFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationText.Get("network.choose-key"),
            AllowMultiple = false,
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.ssh")
        });

        if (files.Count > 0)
        {
            KeyPathBox.Text = files[0].Path.LocalPath;
            KeyPassphraseBox.Text = "";
            RememberKeyPassphrase.IsChecked = false;
        }
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        var server = BuildServerInfo();
        if (server == null) return;
        SetConnecting(true);

        try
        {
            if (_coordinator != null) NavigationPath = await _coordinator.ConnectAsync(server, _connectionCancellation.Token);
            else await _connectionService.ConnectAsync(server, _connectionCancellation.Token);
            _connectionCancellation.Token.ThrowIfCancellationRequested();
            server.LastConnectedAt = DateTimeOffset.UtcNow;
            _connectionService.SaveServer(server);
            Result = server;
            Connected = true;
            Close(server);
        }
        catch (OperationCanceledException) { Connected = false; ConnectionStatus.Text = LocalizationText.Get("network.cancelled"); }
        catch (Exception ex)
        {
            Connected = false;
            if (server.ServiceType == RemoteServiceType.Sftp) _connectionService.Disconnect(server.Id);
            AppToast.ShowError(this, LocalizationText.Get("network.connect-error", ex.Message));
            SystemConnectButton.IsVisible = server.ServiceType != RemoteServiceType.Sftp;
        }
        finally
        {
            SetConnecting(false);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        var server = BuildServerInfo();
        if (server == null) return;
        try
        {
            _connectionService.SaveServer(server);
            Result = server;
            Close(server);
        }
        catch (Exception ex) { AppToast.ShowError(this, LocalizationText.Get("network.save-error", ex.Message)); }
    }

    private void OnRetryCredentials(object? sender, RoutedEventArgs e)
    {
        var selectedId = _editingServer?.Id;
        _connectionService.RetryCredentialMigration();
        if (_connectionService.CredentialLoadError is { } error) AppToast.ShowError(this, error);
        else ConnectionStatus.Text = LocalizationText.Get("network.credentials-reloaded");
        if (selectedId != null && _connectionService.GetSavedServers().FirstOrDefault(server => server.Id == selectedId) is { } reloaded)
        {
            PasswordBox.Text = reloaded.Password;
            KeyPassphraseBox.Text = reloaded.PrivateKeyPassphrase;
            RememberKeyPassphrase.IsChecked = reloaded.RememberPrivateKeyPassphrase;
        }
    }

    private async void OnForgetHostKey(object? sender, RoutedEventArgs e)
    {
        var server = BuildServerInfo();
        if (server == null) return;
        try
        {
            var key = _connectionService.GetTrustedHostKey(server.Host, server.Port);
            if (key == null) { ConnectionStatus.Text = LocalizationText.Get("network.no-trust"); return; }
            var confirm = new ConfirmDialog(MacExplorer.Services.Impl.LocalizationText.Get("撤销主机信任"),MacExplorer.Services.Impl.LocalizationText.Get("{0}:{1}\n{2}\n撤销后会断开此主机的连接，下一次连接需要重新核对身份。请先通过独立渠道核实密钥变更。", key.Host, key.Port, key.Fingerprint),MacExplorer.Services.Impl.LocalizationText.Get("撤销信任"));
            if (!await confirm.ShowDialog<bool>(this)) return;
            _connectionService.ForgetHostKey(server.Host, server.Port);
            ConnectionStatus.Text = LocalizationText.Get("network.trust-revoked");
        }
        catch (Exception ex) { AppToast.ShowError(this, ex.Message); }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private RemoteServerInfo? BuildServerInfo()
    {
        var host = HostBox.Text?.Trim();
        HostError.IsVisible = false;
        if (string.IsNullOrEmpty(host))
        {
            AppToast.ShowError(this, LocalizationText.Get("network.host-required"));
            HostBox.Focus();
            return null;
        }

        var port = 0;
        if (_activeType != RemoteServiceType.WebDav && (!int.TryParse(string.IsNullOrWhiteSpace(PortBox.Text) ? (_activeType == RemoteServiceType.Smb ? "445" : "22") : PortBox.Text.Trim(), out port) || port < 1 || port > 65535))
        {
            AppToast.ShowError(this, LocalizationText.Get("network.port-error")); PortBox.Focus(); return null;
        }
        // Editing is a candidate until persistence succeeds; failed saves cannot mutate saved objects.
        var server = new RemoteServerInfo { Id = _editingServer != null && !_editingServer.Id.StartsWith("discovered:", StringComparison.Ordinal) ? _editingServer.Id : Guid.NewGuid().ToString("N"), LastConnectedAt = _editingServer?.LastConnectedAt };
        server.ServiceType = _activeType;
        server.Name = NameBox.Text?.Trim() ?? "";
        server.Host = host!;
        server.Port = port;
        server.Username = string.IsNullOrWhiteSpace(UsernameBox.Text) ? (_activeType == RemoteServiceType.Sftp ? "root" : "") : UsernameBox.Text.Trim();
        server.DefaultPath = string.IsNullOrWhiteSpace(DefaultPathBox.Text) ? "/" : DefaultPathBox.Text.Trim();
        server.AuthMethod = _activeType != RemoteServiceType.Sftp || PasswordRadio.IsChecked == true ? RemoteAuthMethod.Password : RemoteAuthMethod.PrivateKey;
        server.Password = server.AuthMethod == RemoteAuthMethod.Password ? PasswordBox.Text ?? "" : "";
        server.PrivateKeyPath = KeyPathBox.Text?.Trim() ?? "";
        server.PrivateKeyPassphrase = server.AuthMethod == RemoteAuthMethod.PrivateKey ? KeyPassphraseBox.Text ?? "" : "";
        server.RememberPrivateKeyPassphrase = server.AuthMethod == RemoteAuthMethod.PrivateKey && RememberKeyPassphrase.IsChecked == true;
        if (_activeType == RemoteServiceType.Smb)
        {
            try
            {
                var address = SmbAddress.Parse(server.Host, server.Port, ShareBox.Text ?? "", server.DefaultPath);
                server.Host = address.Host; server.Port = address.Port; server.ShareName = address.Share; server.DefaultPath = address.Path;
                server.Domain = DomainBox.Text?.Trim() ?? "";
                server.PrivateKeyPath = ""; server.PrivateKeyPassphrase = ""; server.RememberPrivateKeyPassphrase = false;
            }
            catch (ArgumentException ex) { AppToast.ShowError(this, ex.Message); return null; }
        }
        if (_activeType == RemoteServiceType.WebDav)
        {
            try
            {
                var address = WebDavAddress.Parse(host);
                server.WebDavUrl = address.Uri.AbsoluteUri;
                server.Host = address.Uri.IdnHost; server.Port = address.Uri.Port;
                server.DefaultPath = "/";
                server.PrivateKeyPath = "";
            }
            catch (ArgumentException ex) { AppToast.ShowError(this, ex.Message); return null; }
        }
        return server;
    }

    private void OnServiceTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingType || HostBox == null || ShareBox == null || _connecting) return;
        var type = (RemoteServiceType)Math.Max(0, ServiceTypeBox.SelectedIndex);
        if (type == _activeType) return;
        var current = CaptureDraft();
        _protocolDrafts[_activeType] = current;
        LoadDraft(_protocolDrafts.GetValueOrDefault(type) ?? current with
            { Type = type, Host = "", Port = type == RemoteServiceType.Smb ? "445" : "22", UseKey = false });
    }

    private void UpdateProtocolVisibility()
    {
        var smb = _activeType == RemoteServiceType.Smb;
        var sftp = _activeType == RemoteServiceType.Sftp;
        var webdav = _activeType == RemoteServiceType.WebDav;
        SmbParameters.IsVisible = smb; AuthMethodPanel.IsVisible = sftp; ForgetHostKeyButton.IsVisible = sftp;
        PortRow.IsVisible = !webdav; DefaultPathRow.IsVisible = !webdav;
        HostLabel.Text = LocalizationText.Get(webdav ? "network.webdav-address" : "ui.515");
        HostBox.PlaceholderText = webdav ? "https://example.com/dav/" : LocalizationText.Get("ui.516");
        Avalonia.Automation.AutomationProperties.SetName(HostBox, HostLabel.Text);
        OnAuthMethodChanged(this, new RoutedEventArgs());
        UsernameBox.PlaceholderText = sftp ? "root" : ""; PortBox.PlaceholderText = smb ? "445" : "22";
        SystemConnectButton.IsVisible = false;
    }

    private async void OnSystemConnect(object? sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        var server = BuildServerInfo();
        if (server == null || server.ServiceType == RemoteServiceType.Sftp) return;
        SetConnecting(true);
        try
        {
            if (!MacNetworkNative.Open(NetworkMountAddress.MountUri(server).AbsoluteUri))
            { AppToast.ShowError(this, LocalizationText.Get("network.system-error")); return; }
            ConnectionStatus.Text = LocalizationText.Get("network.select-mount");
            var folders = await StorageProvider.OpenAuthorizedFolderPickerAsync(new FolderPickerOpenOptions
                { Title = LocalizationText.Get("network.select-mount"), AllowMultiple = false });
            if (folders.Count == 0) return;
            _connectionCancellation.Token.ThrowIfCancellationRequested();
            var path = folders[0].Path.LocalPath;
            if (!App.Services.GetRequiredService<INetworkMountService>().GetMountedVolumes().Any(v => DirectoryAccess.IsWithin(path, v.Path) && NetworkMountAddress.MatchesEndpoint(server, v.SourceUrl)))
            { AppToast.ShowError(this, LocalizationText.Get("network.select-network-mount")); return; }
            _coordinator?.AdoptMount(server, path);
            server.LastConnectedAt = DateTimeOffset.UtcNow;
            _connectionService.SaveServer(server);
            NavigationPath = path; Result = server; Connected = true; Close(server);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppToast.ShowError(this, LocalizationText.Get("network.connect-error", ex.Message)); }
        finally { SetConnecting(false); }
    }
    public string? NavigationPath { get; private set; }

}
