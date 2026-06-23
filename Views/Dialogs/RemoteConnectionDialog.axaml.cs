using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class RemoteConnectionDialog : DialogWindow
{
    private readonly IRemoteConnectionService _connectionService;
    private RemoteServerInfo? _editingServer;
    public RemoteServerInfo? Result { get; private set; }
    public bool Connected { get; private set; }

    public RemoteConnectionDialog()
    {
        InitializeComponent();
        _connectionService = App.Services.GetRequiredService<IRemoteConnectionService>();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshSavedServers();
        HostBox.Focus();
    }

    private void RefreshSavedServers()
    {
        var servers = _connectionService.GetSavedServers();
        SavedServersList.ItemsSource = servers;
        var hasServers = servers.Count > 0;
        if (SavedServersList.Parent is Border border)
            border.IsVisible = hasServers;
    }

    private void OnSavedServerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SavedServersList.SelectedItem is not RemoteServerInfo server) return;
        _editingServer = server;
        NameBox.Text = server.Name;
        HostBox.Text = server.Host;
        PortBox.Text = server.Port.ToString();
        UsernameBox.Text = server.Username;
        DefaultPathBox.Text = server.DefaultPath;
        DeleteButton.IsVisible = true;

        if (server.AuthMethod == RemoteAuthMethod.PrivateKey)
        {
            KeyRadio.IsChecked = true;
            KeyPathBox.Text = server.PrivateKeyPath;
        }
        else
        {
            PasswordRadio.IsChecked = true;
            PasswordBox.Text = server.Password;
        }
    }

    private void OnAuthMethodChanged(object? sender, RoutedEventArgs e)
    {
        var isPassword = PasswordRadio.IsChecked == true;
        PasswordPanel.IsVisible = isPassword;
        KeyPanel.IsVisible = !isPassword;
    }

    private async void OnBrowseKeyFile(object? sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择私钥文件",
            AllowMultiple = false,
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.ssh")
        });

        if (files.Count > 0)
            KeyPathBox.Text = files[0].Path.LocalPath;
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        var server = BuildServerInfo();
        if (server == null) return;

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "连接中...";

        try
        {
            await _connectionService.ConnectAsync(server);
            _connectionService.SaveServer(server);
            Result = server;
            Connected = true;
            Close(server);
        }
        catch (Exception ex)
        {
            Connected = false;
            // Show error
            var errorDialog = new DialogWindow
            {
                Title = "连接失败",
                Width = 360,
                Height = 200,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new global::Avalonia.Thickness(20),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"无法连接到服务器：", FontSize = 13 },
                        new TextBlock { Text = ex.Message, FontSize = 12, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                                       Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#FF3B30")) },
                        new Button { Content = "确定", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                                    Padding = new global::Avalonia.Thickness(16, 6) }
                    }
                }
            };
            if (errorDialog.Content is StackPanel panel && panel.Children[^1] is Button btn)
                btn.Click += (_, _) => errorDialog.Close();
            await errorDialog.ShowDialog(this);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "连接";
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var server = BuildServerInfo();
        if (server == null) return;
        _connectionService.SaveServer(server);
        RefreshSavedServers();
    }

    private void OnDeleteServer(object? sender, RoutedEventArgs e)
    {
        if (_editingServer == null) return;
        _connectionService.RemoveServer(_editingServer.Id);
        _editingServer = null;
        ClearForm();
        RefreshSavedServers();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private RemoteServerInfo? BuildServerInfo()
    {
        var host = HostBox.Text?.Trim();
        if (string.IsNullOrEmpty(host)) return null;

        var server = _editingServer ?? new RemoteServerInfo();
        server.Name = NameBox.Text?.Trim() ?? "";
        server.Host = host;
        server.Port = int.TryParse(PortBox.Text?.Trim(), out var port) ? port : 22;
        server.Username = UsernameBox.Text?.Trim() ?? "root";
        server.DefaultPath = DefaultPathBox.Text?.Trim() ?? "/";
        server.AuthMethod = PasswordRadio.IsChecked == true ? RemoteAuthMethod.Password : RemoteAuthMethod.PrivateKey;
        server.Password = PasswordBox.Text ?? "";
        server.PrivateKeyPath = KeyPathBox.Text?.Trim() ?? "";
        return server;
    }

    private void ClearForm()
    {
        _editingServer = null;
        NameBox.Text = "";
        HostBox.Text = "";
        PortBox.Text = "";
        UsernameBox.Text = "";
        PasswordBox.Text = "";
        KeyPathBox.Text = "";
        DefaultPathBox.Text = "/";
        PasswordRadio.IsChecked = true;
        DeleteButton.IsVisible = false;
    }
}
