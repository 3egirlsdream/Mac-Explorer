using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

internal static class PluginPackage
{
    private static readonly Regex Identifier = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    public static PluginManifest ReadManifest(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "plugin.json"));
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("插件清单过大。");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(stream, PluginProtocol.Json)
            ?? throw new InvalidDataException("插件清单为空。");
        if (manifest.Id == null || !Identifier.IsMatch(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 ||
            !Version.TryParse(manifest.Version, out _) || !Identifier.IsMatch(manifest.Version))
            throw new InvalidDataException("插件标识、名称或版本无效。");
        if (manifest.ApiVersion < 1 || manifest.ApiVersion > PluginProtocol.ApiVersion) throw new InvalidDataException("插件 API 版本与当前应用不兼容。");
        if (manifest.TrialDays is < 0 or > 3650 || (!manifest.Paid && manifest.TrialDays != 0) ||
            (manifest.ApiVersion == 1 && (manifest.Paid || manifest.HasUserInterface)))
            throw new InvalidDataException("授权或窗口能力需要 API v2，试用天数必须在 0–3650 之间。");
        var entry = ContainedPath(directory, manifest.Entry);
        if (!File.Exists(entry) || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件入口程序集不存在。");
        _ = AssemblyName.GetAssemblyName(entry); // Read metadata without loading plugin code into the UI process.
        if (manifest.Commands is not { Length: > 0 and <= 100 } || manifest.Commands.Any(c => c == null) ||
            manifest.Commands.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Commands.Length)
            throw new InvalidDataException("插件命令为空或存在重复标识。");
        foreach (var command in manifest.Commands)
        {
            if (command.Id == null || !Identifier.IsMatch(command.Id) || string.IsNullOrWhiteSpace(command.Title) || command.Match == null ||
                command.Match.MinSelection < 1 || command.Match.MaxSelection < command.Match.MinSelection || command.Match.MaxSelection > 1000 ||
                command.Match.Extensions == null || command.Match.FileNames == null)
                throw new InvalidDataException("插件命令声明无效。");
        }
        return manifest;
    }

    public static string ContainedPath(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
            throw new InvalidDataException("插件路径无效。");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("插件路径超出允许目录。");
        return path;
    }

    public static async Task ExtractAsync(string package, string directory, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 512L * 1024 * 1024)
            throw new InvalidDataException("插件包过大。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = ContainedPath(directory, entry.FullName);
            if (!names.Add(path)) throw new InvalidDataException("插件包存在重复路径。");
            var mode = (entry.ExternalAttributes >> 16) & 0xffff;
            var kind = mode & 0xf000;
            if (kind != 0 && kind != 0x8000 && kind != 0x4000) throw new InvalidDataException("插件包不允许符号链接或特殊文件。");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536]; long copied = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    copied += count;
                    if (copied > entry.Length) throw new InvalidDataException("插件包解压长度不匹配。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (copied != entry.Length) throw new InvalidDataException("插件包解压不完整。");
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    ((mode & 0x49) != 0 ? UnixFileMode.UserExecute : 0));
        }
    }
}
