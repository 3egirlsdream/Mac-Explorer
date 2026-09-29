using Avalonia.Controls;
using Avalonia.Interactivity;
using MacExplorer.Controls;
using MacExplorer.Services;

namespace MacExplorer.Views.Dialogs;

public partial class LocalSendAddressDialog : DialogWindow
{
    private readonly ILocalSendService _service;
    private readonly CancellationTokenSource _closed = new();
    private bool _connected;
    private bool _connecting;

    public LocalSendAddressDialog(ILocalSendService service)
    {
        _service = service;
        InitializeComponent();
        Opened += (_, _) => AddressBox.Focus();
        Closed += (_, _) =>
        {
            _closed.Cancel();
            if (!_connecting) _closed.Dispose();
        };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        if (_connected) { Close(true); return; }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = "端口须为 1–65535。";
            return;
        }
        _connecting = true;
        ConnectButton.IsEnabled = false;
        AddressBox.IsEnabled = false;
        PortBox.IsEnabled = false;
        StatusText.Text = "正在连接…";
        try
        {
            var peer = await _service.ConnectByAddressAsync(AddressBox.Text?.Trim() ?? "", port, _closed.Token);
            if (_closed.IsCancellationRequested) return;
            _connected = true;
            StatusText.Text = $"已连接 {peer.Alias}。重新打开菜单选择设备发送。";
            ConnectButton.Content = "完成";
            CancelButton.Content = "关闭";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { return; }
        catch (OperationCanceledException)
        {
            StatusText.Text = _service.Enabled ? "连接超时。" : "LocalSend 已关闭。";
        }
        catch (Exception ex) { StatusText.Text = "连接失败：" + ex.Message; }
        finally
        {
            _connecting = false;
            if (_closed.IsCancellationRequested) _closed.Dispose();
            else
            {
                ConnectButton.IsEnabled = true;
                AddressBox.IsEnabled = !_connected;
                PortBox.IsEnabled = !_connected;
            }
        }
    }
}
