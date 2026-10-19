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
    private readonly Dictionary<string, ServerDraft> _protocolDrafts = new();
    private RemoteServerInfo? _editingServer;
    private const string NewDraftId = "new-draft";
    private readonly Dictionary<string, ServerDraft> _drafts = new();
    private bool _refreshingServers;
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
        Loaded += OnLoaded;
        Closed += (_, _) => _connectionCancellation.Cancel();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshSavedServers();
        if (_initialServer != null)
        {
            _editingServer = _connectionService.GetSavedServers().FirstOrDefault(s => s.Id == _initialServer.Id);
            LoadDraft(DraftForServer(_initialServer));
            DeleteButton.IsEnabled = _editingServer != null;
        }
        else SavedServersList.SelectedIndex = _connectionService.GetSavedServers().Count > 0 ? 0 : -1;
        if (_connectionService.CredentialLoadError is { } error) AppToast.ShowError(this, error);
        HostBox.Focus();
    }

    private void RefreshSavedServers()
    {
        var selectedId = _editingServer?.Id;
        _refreshingServers = true;
        try
        {
            var servers = _connectionService.GetSavedServers();
            SavedServersList.ItemsSource = servers;
            SavedServersList.SelectedItem = servers.FirstOrDefault(server => server.Id == selectedId);
        }
        finally { _refreshingServers = false; }
    }

    private void RememberDraft()
    {
        _drafts[_editingServer?.Id ?? NewDraftId] = CaptureDraft();
    }

    private ServerDraft CaptureDraft() => new(NameBox.Text ?? "", HostBox.Text ?? "", PortBox.Text ?? "", UsernameBox.Text ?? "",
        PasswordBox.Text ?? "", KeyPathBox.Text ?? "", KeyPassphraseBox.Text ?? "",
        RememberKeyPassphrase.IsChecked == true, DefaultPathBox.Text ?? "", KeyRadio.IsChecked == true,
        _activeType, ShareBox.Text ?? "", DomainBox.Text ?? "");

    private static ServerDraft DraftForServer(RemoteServerInfo server) => new(server.Name, server.Host,
        server.Port.ToString(), server.Username, server.Password, server.PrivateKeyPath,
        server.PrivateKeyPassphrase, server.RememberPrivateKeyPassphrase, server.DefaultPath,
        server.AuthMethod == RemoteAuthMethod.PrivateKey, server.ServiceType, server.ShareName, server.Domain);

    private void OnSavedServerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingServers || _connecting || SavedServersList.SelectedItem is not RemoteServerInfo server) return;
        RememberDraft();
        _editingServer = server;
        var draft = _drafts.GetValueOrDefault(server.Id) ?? DraftForServer(server);
        LoadDraft(draft);
        DeleteButton.IsEnabled = true;
    }

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

    private void OnAddServer(object? sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        RememberDraft();
        ClearForm();
        SavedServersList.SelectedItem = null;
        if (_drafts.TryGetValue(NewDraftId, out var draft)) LoadDraft(draft);
        ConnectionStatus.Text = "";
        HostBox.Focus();
    }

    private void SetConnecting(bool value)
    {
        _connecting = value;
        SavedServersPanel.IsEnabled = !value;
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
        var smb = _activeType == RemoteServiceType.Smb;
        var isPassword = smb || PasswordRadio.IsChecked == true;
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
        RememberDraft();
        SetConnecting(true);

        try
        {
            if (_coordinator != null) await _coordinator.ConnectAsync(server, _connectionCancellation.Token);
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
            SystemConnectButton.IsVisible = server.ServiceType == RemoteServiceType.Smb;
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
            _drafts.Remove(_editingServer?.Id ?? NewDraftId);
            _editingServer = server;
            RememberDraft();
            DeleteButton.IsEnabled = true;
            RefreshSavedServers();
            ConnectionStatus.Text = LocalizationText.Get("network.saved");
        }
        catch (Exception ex) { AppToast.ShowError(this, LocalizationText.Get("network.save-error", ex.Message)); }
    }

    private void OnDeleteServer(object? sender, RoutedEventArgs e)
    {
        if (_connecting || _editingServer == null) return;
        try
        {
            _connectionService.RemoveServer(_editingServer.Id);
            _drafts.Remove(_editingServer.Id);
            _editingServer = null;
            ClearForm();
            RefreshSavedServers();
            if (_drafts.TryGetValue(NewDraftId, out var draft)) LoadDraft(draft);
            ConnectionStatus.Text = LocalizationText.Get("network.deleted");
        }
        catch (Exception ex) { AppToast.ShowError(this, LocalizationText.Get("network.delete-error", ex.Message)); }
    }

    private void OnRetryCredentials(object? sender, RoutedEventArgs e)
    {
        var selectedId = _editingServer?.Id;
        _connectionService.RetryCredentialMigration();
        if (_connectionService.CredentialLoadError is { } error) AppToast.ShowError(this, error);
        else ConnectionStatus.Text = LocalizationText.Get("network.credentials-reloaded");
        RefreshSavedServers();
        if (selectedId != null && _connectionService.GetSavedServers().FirstOrDefault(server => server.Id == selectedId) is { } reloaded)
        {
            PasswordBox.Text = reloaded.Password;
            KeyPassphraseBox.Text = reloaded.PrivateKeyPassphrase;
            RememberKeyPassphrase.IsChecked = reloaded.RememberPrivateKeyPassphrase;
            RememberDraft();
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

        if (!int.TryParse(string.IsNullOrWhiteSpace(PortBox.Text) ? (_activeType == RemoteServiceType.Smb ? "445" : "22") : PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            AppToast.ShowError(this, LocalizationText.Get("network.port-error")); PortBox.Focus(); return null;
        }
        // Editing is a candidate until persistence succeeds; failed saves cannot mutate saved objects.
        var server = new RemoteServerInfo { Id = _editingServer?.Id ?? Guid.NewGuid().ToString("N"), LastConnectedAt = _editingServer?.LastConnectedAt };
        server.ServiceType = _activeType;
        server.Name = NameBox.Text?.Trim() ?? "";
        server.Host = host!;
        server.Port = port;
        server.Username = string.IsNullOrWhiteSpace(UsernameBox.Text) ? (_activeType == RemoteServiceType.Smb ? "" : "root") : UsernameBox.Text.Trim();
        server.DefaultPath = string.IsNullOrWhiteSpace(DefaultPathBox.Text) ? "/" : DefaultPathBox.Text.Trim();
        server.AuthMethod = _activeType == RemoteServiceType.Smb || PasswordRadio.IsChecked == true ? RemoteAuthMethod.Password : RemoteAuthMethod.PrivateKey;
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
        return server;
    }

    private void OnServiceTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingType || HostBox == null || ShareBox == null || _connecting) return;
        var type = ServiceTypeBox.SelectedIndex == 1 ? RemoteServiceType.Smb : RemoteServiceType.Sftp;
        if (type == _activeType) return;
        var key = _editingServer?.Id ?? NewDraftId;
        var current = CaptureDraft();
        _protocolDrafts[$"{key}:{_activeType}"] = current;
        LoadDraft(_protocolDrafts.GetValueOrDefault($"{key}:{type}") ?? current with
            { Type = type, Port = type == RemoteServiceType.Smb ? "445" : "22", UseKey = false });
    }

    private void UpdateProtocolVisibility()
    {
        var smb = _activeType == RemoteServiceType.Smb;
        SmbParameters.IsVisible = smb; AuthMethodPanel.IsVisible = !smb; ForgetHostKeyButton.IsVisible = !smb;
        UsernameBox.PlaceholderText = smb ? "" : "root"; PortBox.PlaceholderText = smb ? "445" : "22";
        SystemConnectButton.IsVisible = false;
    }

    private async void OnSystemConnect(object? sender, RoutedEventArgs e)
    {
        var server = BuildServerInfo();
        if (server == null || server.ServiceType != RemoteServiceType.Smb) return;
        if (!MacNetworkNative.Open(SmbAddress.FromServer(server).MountUri.AbsoluteUri))
        { AppToast.ShowError(this, LocalizationText.Get("network.system-error")); return; }
        ConnectionStatus.Text = LocalizationText.Get("network.select-mount");
        var folders = await StorageProvider.OpenAuthorizedFolderPickerAsync(new FolderPickerOpenOptions
            { Title = LocalizationText.Get("network.select-mount"), AllowMultiple = false });
        if (folders.Count == 0) return;
        var path = folders[0].Path.LocalPath;
        if (!App.Services.GetRequiredService<ISmbMountService>().GetMountedVolumes().Any(v => DirectoryAccess.IsWithin(path, v.Path) && SmbAddress.FromServer(server).Matches(v.SourceUrl, requireShare: !string.IsNullOrEmpty(server.ShareName))))
        { AppToast.ShowError(this, LocalizationText.Get("network.select-network-mount")); return; }
        try
        {
            _coordinator?.AdoptMount(server, path);
            server.LastConnectedAt = DateTimeOffset.UtcNow;
            _connectionService.SaveServer(server);
            NavigationPath = path; Result = server; Connected = true; Close(server);
        }
        catch (Exception ex) { AppToast.ShowError(this, LocalizationText.Get("network.connect-error", ex.Message)); }
    }
    public string? NavigationPath { get; private set; }

    private void ClearForm()
    {
        _editingServer = null;
        _activeType = RemoteServiceType.Sftp;
        ServiceTypeBox.SelectedIndex = 0;
        ShareBox.Text = ""; DomainBox.Text = "";
        UpdateProtocolVisibility();
        NameBox.Text = "";
        HostBox.Text = "";
        PortBox.Text = "";
        UsernameBox.Text = "";
        PasswordBox.Text = "";
        KeyPathBox.Text = "";
        KeyPassphraseBox.Text = "";
        RememberKeyPassphrase.IsChecked = false;
        DefaultPathBox.Text = "/";
        PasswordRadio.IsChecked = true;
        DeleteButton.IsEnabled = false;
    }
}
