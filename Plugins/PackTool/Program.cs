using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using MacExplorer.PluginSdk;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法: dotnet MacExplorer.PluginPack.dll <构建输出目录> <输出.mexplug>");
    return 2;
}
string? temporary = null;
try
{
    var root = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var target = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root) || !target.EndsWith(".mexplug", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith(root, StringComparison.Ordinal) || File.Exists(target))
        throw new InvalidDataException("源目录或输出路径无效；输出必须在源目录之外，且不能覆盖已有文件。");
    var files = new List<(string Path, string Relative)>();
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pending = new Stack<string>(); pending.Push(root);
    long total = 0;
    while (pending.TryPop(out var directory))
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不允许符号链接。");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不允许符号链接。");
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains(':') || relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or "..") || !names.Add(relative))
                throw new InvalidDataException("文件路径无效或重名。");
            if (names.Count > 10000) throw new InvalidDataException("文件数量超过 10000。");
            if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
            total += new FileInfo(path).Length;
            if (total > 512L * 1024 * 1024) throw new InvalidDataException("插件内容超过 512 MB。");
            files.Add((path, relative));
        }
    }
    var manifestPath = Path.Combine(root, "plugin.json");
    if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 1024 * 1024) throw new InvalidDataException("缺少 plugin.json 或清单过大。");
    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), PluginProtocol.Json)
        ?? throw new InvalidDataException("清单为空。");
    bool Identifier(string? value) => value != null && Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$");
    if (!Identifier(manifest.Id) || !Identifier(manifest.Version) || !Version.TryParse(manifest.Version, out _) ||
        string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 || manifest.ApiVersion is < 1 or > PluginProtocol.ApiVersion ||
        manifest.TrialDays is < 0 or > 3650 || (!manifest.Paid && manifest.TrialDays != 0) ||
        (manifest.ApiVersion == 1 && (manifest.Paid || manifest.HasUserInterface)) || manifest.Platform != "osx" ||
        manifest.Architecture is not ("arm64" or "x64")) throw new InvalidDataException("清单身份、平台或 API 声明无效。");
    if (manifest.Commands is not { Length: > 0 and <= 100 } || manifest.Commands.Any(c => c == null ||
        !Identifier(c.Id) || string.IsNullOrWhiteSpace(c.Title) || c.Match == null || c.Match.Extensions == null || c.Match.FileNames == null ||
        c.Match.MinSelection < 1 || c.Match.MaxSelection < c.Match.MinSelection || c.Match.MaxSelection > 1000) ||
        manifest.Commands.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Commands.Length)
        throw new InvalidDataException("命令声明无效。");
    var entry = files.SingleOrDefault(f => f.Relative == manifest.Entry);
    if (entry.Path == null || !entry.Relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("入口程序集不存在。");
    using (var stream = File.OpenRead(entry.Path))
    using (var pe = new PEReader(stream))
        if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly) throw new InvalidDataException("入口不是 .NET 程序集。");
    if (!File.Exists(Path.ChangeExtension(entry.Path, ".deps.json"))) throw new InvalidDataException("缺少入口依赖清单，请启用 EnableDynamicLoading 后构建。");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
    using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        foreach (var file in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            var item = archive.CreateEntryFromFile(file.Path, file.Relative, CompressionLevel.Optimal);
            if (!OperatingSystem.IsWindows()) item.ExternalAttributes = (0x8000 | (int)File.GetUnixFileMode(file.Path)) << 16;
        }
    File.Move(temporary, target); temporary = null;
    Console.WriteLine(target);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
