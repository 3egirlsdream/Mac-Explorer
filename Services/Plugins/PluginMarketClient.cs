using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

public sealed record PluginMarketItem(PluginManifest Manifest, string DownloadUrl, long Size, string Sha256);
public sealed record PluginMarketPage(PluginMarketItem[] Items, bool HasMore);

public sealed class PluginMarketClient(HttpClient http, string? baseUrl = null)
{
    public string BaseUrl { get; } = baseUrl ?? Environment.GetEnvironmentVariable("MACEXPLORER_MARKET_URL") ?? "https://thankful.top/api/PluginMarket/";
    public async Task<PluginMarketPage> ListAsync(string keyword, int page, CancellationToken token)
    {
        var url = BaseUrl.TrimEnd('/') + "/List?platform=osx&architecture=" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
            + "&apiVersion=" + PluginProtocol.ApiVersion + "&keyword=" + Uri.EscapeDataString(keyword) + "&page=" + page;
        RequireHttps(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await http.GetFromJsonAsync<PluginMarketPage>(url, PluginProtocol.Json, timeout.Token)
            ?? throw new InvalidDataException("市场未返回目录。");
    }

    internal static void RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http")))
            throw new InvalidDataException("市场必须使用 HTTPS 地址。");
    }

    public async Task InstallAsync(PluginMarketItem item, PluginManager manager, IProgress<double?> progress, CancellationToken token)
    {
        RequireHttps(item.DownloadUrl);
        if (item.Manifest == null || item.Size <= 0 || item.Size > 512L * 1024 * 1024 || item.Sha256 is not { Length: 64 }) throw new InvalidDataException("插件下载信息无效。");
        var staging = Path.Combine(manager.RootDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var package = Path.Combine(staging, "package.mexplug");
            using var response = await http.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            RequireHttps(response.RequestMessage!.RequestUri!.AbsoluteUri);
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var buffer = new byte[65536]; long length = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    length += count; if (length > item.Size) throw new InvalidDataException("插件包大小不匹配。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token); progress.Report(length * 100d / item.Size);
                }
                if (length != item.Size) throw new InvalidDataException("插件包下载不完整。");
                output.Position = 0;
                if (!Convert.ToHexString(await SHA256.HashDataAsync(output, token)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件包校验失败。");
            }
            await manager.InstallAsync(package, token, fromMarket: true, expectedManifest: item.Manifest);
        }
        finally { Directory.Delete(staging, true); }
    }
}
