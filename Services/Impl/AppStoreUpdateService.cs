using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public sealed class AppStoreUpdateService(HttpClient http) : IAppUpdateService
{
    private const long AppId = 6820338798;
    private const string BundleIdentifier = "com.thankful.top.macexplorer.store";
    private static readonly Uri StorePage = new($"macappstore://itunes.apple.com/app/id{AppId}?mt=12");
    internal static Uri ReviewPageUri { get; } = new(StorePage.AbsoluteUri + "&action=write-review");

    public string CurrentVersion => AppUpdateService.FormatVersion(AppUpdateService.GetCurrentVersion());
    public Uri? UpdatePageUri => StorePage;

    public async Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default)
    {
        var latest = await GetVersionDetailsAsync(ct).ConfigureAwait(false);
        return latest != null && AppUpdateService.TryParseVersion(latest.Version, out var version)
            && version > AppUpdateService.GetCurrentVersion() ? latest : null;
    }

    public async Task<VersionInfo?> GetVersionDetailsAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var country = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant();
        var response = await http.GetFromJsonAsync<LookupResponse>(
            $"https://itunes.apple.com/lookup?id={AppId}&entity=macSoftware&country={country}", timeout.Token)
            .ConfigureAwait(false);
        var app = response?.Results?.FirstOrDefault(result =>
            result.TrackId == AppId && result.BundleId == BundleIdentifier);
        if (app == null || !AppUpdateService.TryParseVersion(app.Version, out _))
            throw new InvalidOperationException(LocalizationText.Get("updates.store_unavailable"));

        return new VersionInfo
        {
            Version = app.Version!,
            Memo = app.ReleaseNotes ?? "",
            DateTime = app.CurrentVersionReleaseDate ?? ""
        };
    }

    public Task DownloadAndInstallAsync(VersionInfo versionInfo,
        IProgress<(double Progress, string Status)>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException(LocalizationText.Get("updates.store_install_required"));

    private sealed class LookupResponse
    {
        [JsonPropertyName("results")]
        public List<StoreApp>? Results { get; set; }
    }

    private sealed class StoreApp
    {
        [JsonPropertyName("trackId")]
        public long TrackId { get; set; }
        [JsonPropertyName("bundleId")]
        public string? BundleId { get; set; }
        [JsonPropertyName("version")]
        public string? Version { get; set; }
        [JsonPropertyName("releaseNotes")]
        public string? ReleaseNotes { get; set; }
        [JsonPropertyName("currentVersionReleaseDate")]
        public string? CurrentVersionReleaseDate { get; set; }
    }
}
