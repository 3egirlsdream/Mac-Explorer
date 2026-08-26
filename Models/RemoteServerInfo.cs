using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MacExplorer.Models;

public class RemoteServerInfo : INotifyPropertyChanged
{
    private bool _isConnected;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Sftp;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public RemoteAuthMethod AuthMethod { get; set; } = RemoteAuthMethod.Password;
    public string Password { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public string DefaultPath { get; set; } = "/";

    // Aliyun OSS
    /// <summary>OSS endpoint, e.g. oss-cn-hangzhou.aliyuncs.com</summary>
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string AccessKeySecret { get; set; } = "";

    [JsonIgnore]
    public bool IsOss => Protocol == RemoteProtocol.AliyunOss;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            return IsOss ? Bucket : $"{Username}@{Host}";
        }
    }

    public string ConnectionString => IsOss ? $"{Bucket} · {Endpoint}" : $"{Username}@{Host}:{Port}";

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

/// <summary>
/// Remote backend protocol. Sftp is 0 so servers saved before OSS support
/// keep deserializing as SFTP.
/// </summary>
public enum RemoteProtocol
{
    Sftp = 0,
    AliyunOss = 1
}
