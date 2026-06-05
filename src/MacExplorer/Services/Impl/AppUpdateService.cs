using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using Foundation;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public class AppUpdateService : IAppUpdateService
{
    private readonly HttpClient _http;
    private const string VersionApiUrl =
        "http://thankful.top:4396/api/CloudSync/GetVersion?Client=MacExplorer";

    public AppUpdateService(HttpClient http)
    {
        _http = http;
    }

    public async Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default)
    {
        var response = await _http.GetFromJsonAsync<VersionCheckResponse>(
            VersionApiUrl, ct);

        if (response?.Success != true || response.Data == null)
            return null;

        if (string.IsNullOrWhiteSpace(response.Data.Version))
            return null;

        var current = GetCurrentVersion();
        if (!Version.TryParse(response.Data.Version, out var latest))
            return null;

        return latest > current ? response.Data : null;
    }

    public async Task DownloadAndInstallAsync(
        VersionInfo versionInfo,
        IProgress<(double Progress, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MacExplorer_Update");

        // Clean up any leftover from a previous failed attempt
        TryDeleteDirectory(tempDir);
        Directory.CreateDirectory(tempDir);

        // Determine download file name & whether it's a zip
        var downloadUrl = versionInfo.Path;
        var urlFileName = System.IO.Path.GetFileName(
            new Uri(downloadUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(urlFileName))
            urlFileName = "update.bin";

        var isZip = urlFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var downloadPath = System.IO.Path.Combine(tempDir, urlFileName);
        var extractDir = System.IO.Path.Combine(tempDir, "extracted");

        // ── Download ──
        ct.ThrowIfCancellationRequested();
        using var response = await _http.GetAsync(
            downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Also check Content-Type header for zip
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!isZip && contentType != null)
        {
            isZip = contentType.Equals("application/zip", StringComparison.OrdinalIgnoreCase)
                 || contentType.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase);
        }

        var total = response.Content.Headers.ContentLength ?? -1L;

        // Write to .part temp file first, then atomically rename to final path
        var tmpDownloadPath = downloadPath + ".part";
        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = File.Create(tmpDownloadPath))
        {
            var buffer = new byte[8192];
            var downloaded = 0L;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                {
                    var pct = (double)downloaded / total * 100;
                    var mbDown = downloaded / 1024.0 / 1024.0;
                    var mbTotal = total / 1024.0 / 1024.0;
                    progress?.Report((pct, $"下载中 {mbDown:F1} / {mbTotal:F1} MB"));
                }
            }
        }

        // Move .part → final name
        if (File.Exists(downloadPath))
            File.Delete(downloadPath);
        File.Move(tmpDownloadPath, downloadPath);

        ct.ThrowIfCancellationRequested();

        // ── Determine the .app bundle ──
        string appBundle;

        if (isZip)
        {
            progress?.Report((100, "正在解压..."));
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(downloadPath, extractDir);

            appBundle = FindAppBundle(extractDir)
                ?? throw new InvalidOperationException("更新包中未找到 .app 文件");
        }
        else
        {
            // Check magic bytes for zip (PK header)
            if (IsZipFile(downloadPath))
            {
                progress?.Report((100, "正在解压..."));
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(downloadPath, extractDir);

                appBundle = FindAppBundle(extractDir)
                    ?? throw new InvalidOperationException("更新包中未找到 .app 文件");
            }
            else if (downloadPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                  && Directory.Exists(downloadPath))
            {
                appBundle = downloadPath;
            }
            else
            {
                throw new InvalidOperationException(
                    $"无法识别的更新文件格式: {urlFileName}。期望 .zip 或 .app 文件。");
            }
        }

        ct.ThrowIfCancellationRequested();
        EnsureLaunchableAppBundle(appBundle);

        // ── Stage the new .app next to the current one ──
        var currentAppPath = NSBundle.MainBundle.BundlePath;
        var stagedPath = currentAppPath + ".new";

        if (Directory.Exists(stagedPath))
            Directory.Delete(stagedPath, true);
        Directory.Move(appBundle, stagedPath);

        ct.ThrowIfCancellationRequested();

        // ── Write and launch replace script ──
        var scriptPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mac_explorer_update.sh");
        var scriptDir = System.IO.Path.GetDirectoryName(scriptPath)!;

        var script = $@"#!/bin/bash
# mac_explorer_update.sh — runs detached via nohup.
# Waits for the old MacExplorer process to exit, then swaps in the new .app.

PARENT_PID={System.Environment.ProcessId}
STAGED=""{stagedPath}""
CURRENT=""{currentAppPath}""
TEMP=""{tempDir}""
LOG=""/tmp/mac_explorer_update.log""

exec >> ""$LOG"" 2>&1
echo ""[$(date)] Starting MacExplorer update""

# Spin until the parent process is gone (max ~30 s).
for i in $(seq 1 60); do
    if ! kill -0 ""$PARENT_PID"" 2>/dev/null; then
        break
    fi
    sleep 0.5
done

# Small extra delay for the OS to release file handles.
sleep 1

if [ -d ""$STAGED"" ]; then
    if ! rm -rf ""$CURRENT""; then
        echo ""Failed to remove current app: $CURRENT""
        exit 1
    fi

    if ! mv ""$STAGED"" ""$CURRENT""; then
        echo ""Failed to move staged app into place: $STAGED -> $CURRENT""
        exit 1
    fi

    if [ -d ""$CURRENT"" ]; then
        /usr/bin/open ""$CURRENT"" || echo ""Failed to open updated app: $CURRENT""
    fi
else
    echo ""Staged app does not exist: $STAGED""
    exit 1
fi

rm -f ""$0"" 2>/dev/null || true
rm -rf ""$TEMP"" 2>/dev/null || true
";

        File.WriteAllText(scriptPath, script);

        using var chmod = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            Arguments = $"+x \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        chmod?.WaitForExit();

        // Launch script fully detached using nohup + background fork.
        // /bin/sh -c "nohup ... &" starts the script, the shell exits immediately,
        // and the nohup'd child keeps running even after we call Environment.Exit.
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-c \"cd '{scriptDir}' && nohup /bin/bash '{scriptPath}' </dev/null >/dev/null 2>&1 &\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        // ── Quit immediately ──
        System.Environment.Exit(0);
    }

    private static void EnsureLaunchableAppBundle(string appBundle)
    {
        var infoPlistPath = System.IO.Path.Combine(
            appBundle, "Contents", "Info.plist");
        if (!File.Exists(infoPlistPath))
            throw new InvalidOperationException("更新包中的 .app 缺少 Contents/Info.plist");

        var executableName = ReadBundleExecutableName(infoPlistPath);
        if (string.IsNullOrWhiteSpace(executableName))
            throw new InvalidOperationException("更新包中的 .app 缺少 CFBundleExecutable");

        var executablePath = System.IO.Path.Combine(
            appBundle, "Contents", "MacOS", executableName);
        if (!File.Exists(executablePath))
            throw new InvalidOperationException(
                $"更新包中的主程序不存在: Contents/MacOS/{executableName}");

        using var chmod = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "+x", executablePath },
        });
        chmod?.WaitForExit();

        if (chmod?.ExitCode != 0)
            throw new InvalidOperationException(
                $"无法为更新包主程序添加可执行权限: Contents/MacOS/{executableName}");
    }

    private static string? FindAppBundle(string root)
    {
        return Directory.GetDirectories(root, "*.app", SearchOption.AllDirectories)
            .Where(path => !path
                .Split(System.IO.Path.DirectorySeparatorChar)
                .Contains("__MACOSX"))
            .FirstOrDefault(path => File.Exists(System.IO.Path.Combine(
                path, "Contents", "Info.plist")));
    }

    private static string ReadBundleExecutableName(string infoPlistPath)
    {
        using var plistBuddy = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/libexec/PlistBuddy",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", "Print :CFBundleExecutable", infoPlistPath },
        });

        if (plistBuddy == null)
            return "";

        var output = plistBuddy.StandardOutput.ReadToEnd();
        plistBuddy.WaitForExit();

        return plistBuddy.ExitCode == 0 ? output.Trim() : "";
    }

    private static bool IsZipFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4)
                return false;
            // PK magic bytes
            return header[0] == 0x50 && header[1] == 0x4B
                && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07)
                && header[3] == 0x04;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // ignore — will be cleaned up on next successful update
        }
    }

    internal static Version GetCurrentVersion()
    {
        var str = NSBundle.MainBundle.InfoDictionary["CFBundleShortVersionString"]
            ?.ToString() ?? "1.0";
        return Version.TryParse(str, out var v) ? v : new Version(1, 0);
    }
}
