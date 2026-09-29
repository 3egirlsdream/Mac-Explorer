namespace MacExplorer.Services;

public sealed record LocalSendDevice(string Alias, string Fingerprint, string Address, int Port, string Protocol);
public sealed record LocalSendIncomingFile(string Id, string FileName, long Size);
public sealed record LocalSendIncomingRequest(string Alias, IReadOnlyList<LocalSendIncomingFile> Files, string DefaultDirectory);
public sealed record LocalSendReceiveDecision(bool Accepted, string Directory, IReadOnlyList<string>? AcceptedFileIds = null);

public interface ILocalSendService
{
    const string EnabledKey = "localsend_enabled";
    const string AliasKey = "localsend_alias";
    const string ReceiveDirectoryKey = "localsend_receive_directory";
    const string ScanSubnetKey = "localsend_scan_subnet";
    const string VerifiedPeersKey = "localsend_verified_peers";

    bool Enabled { get; }
    string? LastError { get; }
    int ListeningPort { get; }
    string Alias { get; set; }
    string ReceiveDirectory { get; set; }
    string ScanSubnet { get; set; }
    Func<LocalSendIncomingRequest, CancellationToken, Task<LocalSendReceiveDecision>>? ConfirmReceiveAsync { get; set; }
    Func<CancellationToken, Task<string?>>? RequestPinAsync { get; set; }
    Task StartAsync();
    Task SetEnabledAsync(bool enabled);
    Task<IReadOnlyList<LocalSendDevice>> DiscoverAsync(CancellationToken cancellationToken,
        Action<IReadOnlyList<LocalSendDevice>>? onDevicesChanged = null);
    Task<LocalSendDevice> ConnectByAddressAsync(string address, int port, CancellationToken cancellationToken);
    Task SendAsync(LocalSendDevice device, IReadOnlyList<string> paths);
}
