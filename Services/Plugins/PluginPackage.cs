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
        var manifest = JsonSerializer.Deserialize(stream, PluginJsonContext.Default.PluginManifest)
            ?? throw new InvalidDataException("插件清单为空。");
        if (manifest.Id == null || !Identifier.IsMatch(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 ||
            !Version.TryParse(manifest.Version, out _) || !Identifier.IsMatch(manifest.Version))
            throw new InvalidDataException("插件标识、名称或版本无效。");
        if (manifest.ApiVersion < 1 || manifest.ApiVersion > PluginProtocol.ApiVersion) throw new InvalidDataException("插件 API 版本与当前应用不兼容。");
        if (manifest.TrialDays is < 0 or > 3650 || (!manifest.Paid && manifest.TrialDays != 0) ||
            (manifest.ApiVersion == 1 && (manifest.Paid || manifest.HasUserInterface)))
            throw new InvalidDataException("授权或窗口能力需要 API v2，试用天数必须在 0–3650 之间。");
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

}
