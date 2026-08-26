using Aliyun.OSS;
using Aliyun.OSS.Common;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

/// <summary>
/// Builds <see cref="OssClient"/> instances from a saved server descriptor.
/// OSS has no persistent session — a client is just a signed HTTP caller — so this
/// stays a plain factory instead of living in the connection pool.
/// </summary>
internal static class OssClientFactory
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    public static OssClient Create(RemoteServerInfo server)
    {
        var endpoint = NormalizeEndpoint(server.Endpoint);
        if (endpoint.Length == 0)
            throw new InvalidOperationException("OSS Endpoint 不能为空");
        if (string.IsNullOrWhiteSpace(server.AccessKeyId) || string.IsNullOrWhiteSpace(server.AccessKeySecret))
            throw new InvalidOperationException("OSS AccessKeyId / AccessKeySecret 不能为空");
        if (string.IsNullOrWhiteSpace(server.Bucket))
            throw new InvalidOperationException("OSS Bucket 不能为空");

        var config = new ClientConfiguration
        {
            ConnectionTimeout = (int)RequestTimeout.TotalMilliseconds,
            MaxErrorRetry = 3
        };

        return new OssClient(endpoint, server.AccessKeyId.Trim(), server.AccessKeySecret.Trim(), config);
    }

    /// <summary>
    /// Accepts what users actually paste — "oss-cn-hangzhou.aliyuncs.com",
    /// "https://oss-cn-hangzhou.aliyuncs.com", or a bucket-prefixed endpoint —
    /// and returns the bare region endpoint the SDK expects.
    /// </summary>
    internal static string NormalizeEndpoint(string? endpoint)
    {
        var value = (endpoint ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;

        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = value["https://".Length..];
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            value = value["http://".Length..];

        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];

        return value.TrimEnd('.');
    }
}
