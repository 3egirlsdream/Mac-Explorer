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
    /// "https://oss-cn-hangzhou.aliyuncs.com", or the bucket-prefixed host the OSS
    /// console shows ("my-bucket.oss-cn-hangzhou.aliyuncs.com") — and returns the
    /// bare region endpoint the SDK expects.
    /// </summary>
    internal static string NormalizeEndpoint(string? endpoint)
    {
        var value = StripSchemeAndPath(endpoint);
        if (value.Length == 0) return string.Empty;

        value = value.TrimEnd('.');

        // "my-bucket.oss-cn-hangzhou.aliyuncs.com" → "oss-cn-hangzhou.aliyuncs.com".
        var dot = value.IndexOf('.');
        if (dot > 0 && !value.StartsWith("oss-", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[(dot + 1)..];
            if (rest.StartsWith("oss-", StringComparison.OrdinalIgnoreCase))
                return rest;
        }

        return value;
    }

    /// <summary>
    /// The console shows a bucket as "my-bucket.oss-cn-hangzhou.aliyuncs.com" and
    /// its overview page as "oss://my-bucket/", but a bucket name can never contain
    /// a dot — so anything from the first dot on is an endpoint suffix, not the name.
    /// </summary>
    internal static string NormalizeBucket(string? bucket)
    {
        var value = StripSchemeAndPath(bucket);
        if (value.Length == 0) return string.Empty;

        var dot = value.IndexOf('.');
        return dot < 0 ? value : value[..dot];
    }

    /// <summary>
    /// Turns whatever identifies a starting folder into a bucket-relative path.
    /// "oss://my-bucket/photos" and "photos" both become "/photos"; empty becomes "/".
    /// </summary>
    internal static string NormalizeDefaultPath(string? path, string? bucket)
    {
        var value = (path ?? string.Empty).Trim();
        if (value.Length == 0) return "/";

        var hadOssScheme = value.StartsWith("oss://", StringComparison.OrdinalIgnoreCase);
        if (hadOssScheme) value = value["oss://".Length..];

        if (hadOssScheme)
        {
            // Drop the leading bucket segment that the oss:// form carries.
            var bucketName = NormalizeBucket(bucket);
            var slash = value.IndexOf('/');
            var firstSegment = slash < 0 ? value : value[..slash];
            if (bucketName.Length > 0 && string.Equals(firstSegment, bucketName, StringComparison.Ordinal))
                value = slash < 0 ? string.Empty : value[(slash + 1)..];
        }

        value = value.Replace('\\', '/').Trim('/');
        return value.Length == 0 ? "/" : "/" + value;
    }

    private static string StripSchemeAndPath(string? value)
    {
        var result = (value ?? string.Empty).Trim();
        if (result.Length == 0) return string.Empty;

        foreach (var scheme in (string[])["https://", "http://", "oss://"])
        {
            if (!result.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) continue;
            result = result[scheme.Length..];
            break;
        }

        var slash = result.IndexOf('/');
        if (slash >= 0) result = result[..slash];

        return result.Trim();
    }
}
