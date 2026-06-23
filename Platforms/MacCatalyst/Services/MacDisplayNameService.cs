using System.Collections.Concurrent;
using System.Diagnostics;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Returns localized display names for macOS folders using Spotlight metadata (mdls).
/// This provides Finder-displayed names like "桌面" for ~/Desktop on Chinese macOS.
/// </summary>
public class MacDisplayNameService : IDisplayNameService
{
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public string GetDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // Sentinal paths don't need localization
        if (path.StartsWith("__", StringComparison.Ordinal))
            return string.Empty;

        return _cache.GetOrAdd(path, ResolveDisplayName);
    }

    public string GetUserName()
    {
        // macOS full user display name (e.g., "张三" not "zhangsan")
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var realName = ResolveViaDscl(home);
            if (!string.IsNullOrEmpty(realName))
                return realName;
        }
        catch { }
        return Environment.UserName;
    }

    private static string ResolveDisplayName(string path)
    {
        try
        {
            // Special case: root volume ("/") — use diskutil for the volume name
            if (path == "/")
                return ResolveVolumeName() ?? "Macintosh HD";

            // mdls -name kMDItemDisplayName -raw "/path"
            // Returns the localized display name from Spotlight metadata.
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/mdls",
                    Arguments = $"-name kMDItemDisplayName -raw \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);

            // ExitCode != 0 means the file can't be accessed (SIP-protected, missing, etc.)
            // Also filter out (null) sentinel from mdls
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output) && output != "(null)")
                return output;
        }
        catch { }

        return System.IO.Path.GetFileName(path);
    }

    /// <summary>Get the localized volume name for the root filesystem via diskutil.</summary>
    private static string? ResolveVolumeName()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/diskutil",
                    Arguments = "info /",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            // Parse: "   Volume Name:               Macintosh HD"
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("   Volume Name:", StringComparison.Ordinal))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var name = parts[1].Trim();
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Gets the user's full display name via dscl (Directory Services command line).
    /// This returns the localized/real name like "张三" not the short username "zhangsan".
    /// </summary>
    private static string ResolveViaDscl(string homePath)
    {
        try
        {
            var userName = System.IO.Path.GetFileName(homePath);
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/dscl",
                    Arguments = $". -read /Users/{userName} RealName",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);

            // Output format: "RealName: 张三"
            if (!string.IsNullOrEmpty(output))
            {
                var parts = output.Split(':', 2);
                if (parts.Length == 2)
                {
                    var name = parts[1].Trim();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
        }
        catch { }

        return string.Empty;
    }
}
