using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public class AppUpdateService : IAppUpdateService
{
    private readonly HttpClient _http;
    private const string DefaultBundleIdentifier = "com.macexplorer.app";
    private const string VersionApiUrl =
        "http://thankful.top:4396/api/CloudSync/GetVersion?Client=MacExplorer";

    public AppUpdateService(HttpClient http)
    {
        _http = http;
    }

    public string CurrentVersion => FormatVersion(GetCurrentVersion());

    public async Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default)
    {
        var response = await _http.GetFromJsonAsync<VersionCheckResponse>(
            VersionApiUrl, ct);

        if (response?.Success != true)
            throw new InvalidOperationException("更新服务器返回了失败状态");

        if (response.Data == null)
            throw new InvalidOperationException("更新服务器未返回版本信息");

        if (string.IsNullOrWhiteSpace(response.Data.Version))
            throw new InvalidOperationException("更新服务器返回的版本号为空");

        var current = GetCurrentVersion();
        if (!TryParseVersion(response.Data.Version, out var latest))
            throw new InvalidOperationException($"无法识别服务器版本号: {response.Data.Version}");

        return latest > current ? response.Data : null;
    }

    public async Task DownloadAndInstallAsync(
        VersionInfo versionInfo,
        IProgress<(double Progress, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var currentAppPath = GetCurrentAppBundlePath();
        var currentInfoPlist = Path.Combine(currentAppPath, "Contents", "Info.plist");
        var currentBundleIdentifier = ReadBundleValue(currentInfoPlist, "CFBundleIdentifier");
        if (string.IsNullOrWhiteSpace(currentBundleIdentifier))
            currentBundleIdentifier = DefaultBundleIdentifier;
        var currentExecutableName = ReadBundleValue(currentInfoPlist, "CFBundleExecutable");
        if (string.IsNullOrWhiteSpace(currentExecutableName))
            currentExecutableName = "MacExplorer";

        if (!Uri.TryCreate(versionInfo.Path, UriKind.Absolute, out var downloadUri)
            || (downloadUri.Scheme != Uri.UriSchemeHttps && downloadUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("更新下载地址无效");
        }

        var tempDir = Path.Combine(
            Path.GetTempPath(), $"MacExplorer_Update_{Environment.ProcessId}");

        // Clean up any leftover from a previous failed attempt
        TryDeleteDirectory(tempDir);
        Directory.CreateDirectory(tempDir);

        // Determine download file name & whether it's a zip
        var urlFileName = Path.GetFileName(downloadUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(urlFileName))
            urlFileName = "update.bin";

        var isZip = urlFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var downloadPath = Path.Combine(tempDir, urlFileName);
        var extractDir = Path.Combine(tempDir, "extracted");
        var stagedPath = currentAppPath + ".new";
        var handoffStarted = false;

        try
        {
            ct.ThrowIfCancellationRequested();
            using var response = await _http.GetAsync(
                downloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!isZip && contentType != null)
            {
                isZip = contentType.Equals("application/zip", StringComparison.OrdinalIgnoreCase)
                     || contentType.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase);
            }

            var total = response.Content.Headers.ContentLength ?? -1L;

            var tmpDownloadPath = downloadPath + ".part";
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(
                tmpDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                var downloaded = 0L;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    var mbDown = downloaded / 1024.0 / 1024.0;
                    if (total > 0)
                    {
                        var pct = (double)downloaded / total * 100;
                        var mbTotal = total / 1024.0 / 1024.0;
                        progress?.Report((pct, $"下载中 {mbDown:F1} / {mbTotal:F1} MB"));
                    }
                    else
                    {
                        progress?.Report((-1, $"下载中 {mbDown:F1} MB"));
                    }
                }
            }

            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
            File.Move(tmpDownloadPath, downloadPath);

            ct.ThrowIfCancellationRequested();

            string appBundle;

            if (isZip || IsZipFile(downloadPath))
            {
                progress?.Report((100, "正在解压更新包..."));
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(downloadPath, extractDir);

                appBundle = FindAppBundle(extractDir, currentBundleIdentifier)
                    ?? throw new InvalidOperationException("更新包中未找到 .app 文件");
            }
            else
            {
                throw new InvalidOperationException(
                    $"无法识别的更新文件格式: {urlFileName}。期望 .zip 或 .app 文件。");
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report((100, "正在校验更新包..."));
            EnsureLaunchableAppBundle(
                appBundle,
                currentBundleIdentifier,
                currentExecutableName);

            progress?.Report((100, "正在准备安装..."));
            TryDeleteDirectory(stagedPath);
            if (Directory.Exists(stagedPath))
                throw new InvalidOperationException("无法清理上次遗留的更新文件");
            await RunProcessAsync("/usr/bin/ditto", [appBundle, stagedPath], ct);

            if (!Directory.Exists(stagedPath))
                throw new InvalidOperationException("无法准备更新文件");

            ct.ThrowIfCancellationRequested();

            var scriptPath = Path.Combine(
                Path.GetTempPath(), $"mac_explorer_update_{Environment.ProcessId}.sh");
            var logPath = Path.Combine(
                Path.GetTempPath(), $"mac_explorer_update_{Environment.ProcessId}.log");

            var script = $@"#!/bin/bash

PARENT_PID={System.Environment.ProcessId}
STAGED={ShellQuote(stagedPath)}
CURRENT={ShellQuote(currentAppPath)}
TEMP={ShellQuote(tempDir)}
LOG={ShellQuote(logPath)}
BACKUP=""$CURRENT.update-backup""
SUCCESS=0

exec >> ""$LOG"" 2>&1
echo ""[$(date)] Starting MacExplorer update""

cleanup() {{
    if [ ""$SUCCESS"" -ne 1 ]; then
        rm -rf ""$STAGED"" 2>/dev/null || true
    fi
    rm -rf ""$TEMP"" 2>/dev/null || true
    rm -f ""$0"" 2>/dev/null || true
}}
trap cleanup EXIT

for i in $(seq 1 60); do
    if ! kill -0 ""$PARENT_PID"" 2>/dev/null; then
        break
    fi
    sleep 0.5
done

if kill -0 ""$PARENT_PID"" 2>/dev/null; then
    echo ""Timed out waiting for the current app to exit""
    exit 1
fi

sleep 1

if [ ! -d ""$STAGED"" ]; then
    echo ""Staged app does not exist: $STAGED""
    exit 1
fi

rm -rf ""$BACKUP"" 2>/dev/null || true
if ! mv ""$CURRENT"" ""$BACKUP""; then
    echo ""Failed to back up current app: $CURRENT""
    exit 1
fi

if ! mv ""$STAGED"" ""$CURRENT""; then
    echo ""Failed to move staged app into place; restoring previous app""
    mv ""$BACKUP"" ""$CURRENT"" 2>/dev/null || true
    exit 1
fi

if ! /usr/bin/open ""$CURRENT""; then
    echo ""Failed to open updated app; restoring previous app""
    rm -rf ""$CURRENT"" 2>/dev/null || true
    mv ""$BACKUP"" ""$CURRENT"" 2>/dev/null || true
    /usr/bin/open ""$CURRENT"" 2>/dev/null || true
    exit 1
fi

rm -rf ""$BACKUP"" 2>/dev/null || true
SUCCESS=1
echo ""[$(date)] Update completed""
";

            await File.WriteAllTextAsync(scriptPath, script, ct);

            await RunProcessAsync("/bin/chmod", ["+x", scriptPath], ct);

            var launcher = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-c",
                    $"nohup /bin/bash {ShellQuote(scriptPath)} </dev/null >/dev/null 2>&1 &"
                }
            });
            if (launcher == null)
                throw new InvalidOperationException("无法启动更新安装程序");

            launcher.Dispose();
            handoffStarted = true;
            progress?.Report((100, "更新已下载，正在重启应用..."));
            Environment.Exit(0);
        }
        finally
        {
            if (!handoffStarted)
            {
                TryDeleteDirectory(stagedPath);
                TryDeleteDirectory(tempDir);
            }
        }
    }

    private static void EnsureLaunchableAppBundle(
        string appBundle,
        string expectedBundleIdentifier,
        string expectedExecutableName)
    {
        var infoPlistPath = Path.Combine(
            appBundle, "Contents", "Info.plist");
        if (!File.Exists(infoPlistPath))
            throw new InvalidOperationException("更新包中的 .app 缺少 Contents/Info.plist");

        var bundleIdentifier = ReadBundleValue(infoPlistPath, "CFBundleIdentifier");
        if (!string.Equals(bundleIdentifier, expectedBundleIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"更新包与当前 Avalonia 版本不兼容（{bundleIdentifier}）。" +
                $"需要 Bundle ID 为 {expectedBundleIdentifier} 的更新包。");
        }

        var executableName = ReadBundleValue(infoPlistPath, "CFBundleExecutable");
        if (string.IsNullOrWhiteSpace(executableName))
            throw new InvalidOperationException("更新包中的 .app 缺少 CFBundleExecutable");
        if (!string.Equals(executableName, expectedExecutableName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"更新包主程序与当前版本不兼容（{executableName}）。" +
                $"需要主程序 {expectedExecutableName}。");
        }

        var executablePath = Path.Combine(
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

        using var codesign = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/codesign",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList = { "--verify", "--deep", "--strict", appBundle },
        });
        if (codesign == null)
            throw new InvalidOperationException("无法校验更新包签名");

        var codesignError = codesign.StandardError.ReadToEnd();
        codesign.WaitForExit();
        if (codesign.ExitCode != 0)
            throw new InvalidOperationException(
                $"更新包签名校验失败: {codesignError.Trim()}");
    }

    private static string? FindAppBundle(string root, string expectedBundleIdentifier)
    {
        var bundles = Directory.GetDirectories(root, "*.app", SearchOption.AllDirectories)
            .Where(path => !path
                .Split(Path.DirectorySeparatorChar)
                .Contains("__MACOSX"))
            .Where(path => File.Exists(Path.Combine(path, "Contents", "Info.plist")))
            .OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar))
            .ToList();

        return bundles.FirstOrDefault(path => string.Equals(
                   ReadBundleValue(Path.Combine(path, "Contents", "Info.plist"), "CFBundleIdentifier"),
                   expectedBundleIdentifier,
                   StringComparison.Ordinal))
               ?? bundles.FirstOrDefault();
    }

    private static string ReadBundleValue(string infoPlistPath, string key)
    {
        using var plistBuddy = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/libexec/PlistBuddy",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", $"Print :{key}", infoPlistPath },
        });

        if (plistBuddy == null)
            return "";

        var output = plistBuddy.StandardOutput.ReadToEnd();
        plistBuddy.WaitForExit();

        return plistBuddy.ExitCode == 0 ? output.Trim() : "";
    }

    private static string GetCurrentAppBundlePath()
    {
        var bundlePath = TryGetCurrentAppBundlePath();
        if (bundlePath == null)
        {
            throw new InvalidOperationException(
                "当前程序不是从 macOS .app 中运行，无法自动安装更新。请从应用程序文件夹启动后重试。");
        }

        return bundlePath;
    }

    private static string? TryGetCurrentAppBundlePath()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Environment.ProcessPath,
            System.Reflection.Assembly.GetExecutingAssembly().Location,
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate!)
                : new FileInfo(candidate!).Directory;

            while (directory != null)
            {
                if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(directory.FullName, "Contents", "Info.plist")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static async Task RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException($"无法启动 {fileName}");

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} 执行失败: {detail.Trim()}");
        }
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

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
        var bundlePath = TryGetCurrentAppBundlePath();
        if (bundlePath != null)
        {
            var bundleVersion = ReadBundleValue(
                Path.Combine(bundlePath, "Contents", "Info.plist"),
                "CFBundleShortVersionString");
            if (TryParseVersion(bundleVersion, out var parsedBundleVersion))
                return parsedBundleVersion;
        }

        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (TryParseVersion(informationalVersion, out var parsedInformationalVersion))
            return parsedInformationalVersion;

        var version = assembly.GetName().Version;
        if (version != null && version.Major > 0)
            return version;
        return new Version(1, 0);
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        var normalized = value?.Trim().TrimStart('v', 'V');
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            var metadataIndex = normalized.IndexOfAny(['-', '+']);
            if (metadataIndex >= 0)
                normalized = normalized[..metadataIndex];
        }

        return Version.TryParse(normalized, out version!);
    }

    private static string FormatVersion(Version version)
    {
        if (version.Revision > 0)
            return version.ToString(4);
        if (version.Build > 0)
            return version.ToString(3);
        return version.ToString(2);
    }
}
