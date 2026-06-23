using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MacExplorer.Models;

public class RemoteServerInfo : INotifyPropertyChanged
{
    private bool _isConnected;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public RemoteAuthMethod AuthMethod { get; set; } = RemoteAuthMethod.Password;
    public string Password { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public string DefaultPath { get; set; } = "/";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Host}" : Name;
    public string ConnectionString => $"{Username}@{Host}:{Port}";

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusColor => IsConnected ? "#22C55E" : "#9CA3AF";
    public string StatusText => IsConnected ? "已连接" : "未连接";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum RemoteAuthMethod
{
    Password,
    PrivateKey
}
