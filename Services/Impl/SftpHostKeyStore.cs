using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace MacExplorer.Services.Impl;

public sealed record SftpHostKey(string Host, int Port, string Algorithm, string Fingerprint);

public sealed class SftpHostKeyException(string message) : InvalidOperationException(message);

internal sealed class SftpHostKeyStore(string path)
{
    private readonly object _gate = new();

    internal static string NormalizeHost(string host)
    {
        var value = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(value, out var address)) return address.ToString();
        return new IdnMapping().GetAscii(value.TrimEnd('.')).ToLowerInvariant();
    }

    private static string Identity(string host, int port) => $"[{NormalizeHost(host)}]:{port}";

    private Dictionary<string, SftpHostKey> Read()
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, SftpHostKey>>(File.ReadAllText(path))
                ?? throw new JsonException("empty host-key store");
        }
        catch (Exception ex)
        {
            throw new SftpHostKeyException($"无法读取 SFTP 主机信任记录，连接已停止。请恢复记录文件后重试：{path}（{ex.Message}）");
        }
    }

    internal SftpHostKey? Get(string host, int port)
    {
        lock (_gate) return Read().GetValueOrDefault(Identity(host, port));
    }

    internal async Task VerifyAsync(string host, int port, string algorithm, byte[] key,
        Func<SftpHostKey, CancellationToken, Task<bool>>? confirm, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (key.Length == 0) throw new SftpHostKeyException("服务器未提供有效主机密钥，连接已停止。");
        var observed = new SftpHostKey(NormalizeHost(host), port, algorithm,
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('='));
        lock (_gate)
        {
            var saved = Read().GetValueOrDefault(Identity(host, port));
            if (saved != null) { RequireMatch(saved, observed); return; }
        }
        if (confirm == null || !await confirm(observed, ct))
            throw new SftpHostKeyException("未信任服务器身份，连接已取消；未保存新的主机信任。");
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var records = Read();
            var identity = Identity(host, port);
            if (records.TryGetValue(identity, out var previous)) { RequireMatch(previous, observed); return; }
            ct.ThrowIfCancellationRequested();
            records.Add(identity, observed);
            Write(records); // Only successful persistence permits SSH authentication.
        }
    }

    private static void RequireMatch(SftpHostKey saved, SftpHostKey observed)
    {
        // RSA SHA-256/SHA-512 negotiation can change while the public key stays identical.
        if (saved.Fingerprint == observed.Fingerprint) return;
        throw new SftpHostKeyException($"服务器 {observed.Host}:{observed.Port} 的主机密钥已变化，连接已拒绝。\n已信任：{saved.Fingerprint}\n当前：{observed.Fingerprint}\n请通过独立渠道向管理员核实；确认变更后，在远程连接窗口撤销此主机的旧信任，再重新连接。旧记录未被覆盖。");
    }

    internal void Forget(string host, int port)
    {
        lock (_gate)
        {
            var records = Read();
            if (records.Remove(Identity(host, port))) Write(records);
        }
    }

    private void Write(Dictionary<string, SftpHostKey> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
