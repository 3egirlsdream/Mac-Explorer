using System.IO.Compression;
using System.Text.Json;
using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Plugins;

namespace MacExplorer.Tests;

internal sealed class PluginTestEnvironment : IDisposable
{
    public static string Repository
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "MacExplorer.csproj"))) root = root.Parent;
            return root?.FullName ?? throw new DirectoryNotFoundException("Repository not found.");
        }
    }
    public static string Configuration => AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";
    public static string ApplicationOutput => Path.Combine(Repository, "bin", Configuration, "net10.0", "osx-arm64", "Mac Explorer.app", "Contents", "MacOS");
    public static string BundledPackage => Path.Combine(ApplicationOutput, "BundledPlugins", "FileConversion.mexplug");
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fkfinder-plugin-test-" + Guid.NewGuid().ToString("N"));
    public MemorySettings Settings { get; } = new();
    public PluginManager Manager { get; }
    public PluginTestEnvironment(bool initialize = true)
    {
        Directory.CreateDirectory(Root);
        Manager = NewManager();
        if (initialize) Task.Run(() => Manager.InitializeAsync()).GetAwaiter().GetResult();
    }
    public PluginManager NewManager() => new(Settings, Path.Combine(Root, "Plugins"), BundledPackage,
        Path.Combine(ApplicationOutput, "MacExplorer"), Path.Combine(ApplicationOutput, "MacExplorer.dll"));
    public string Write(string name, string contents)
    {
        var path = Path.Combine(Root, name); File.WriteAllText(path, contents); return path;
    }
    public string CreateFixture(string version = "1.0.0", int apiVersion = 1)
    {
        var directory = Path.Combine(Root, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(Repository, "Tools", "PluginTestFixture", "bin", Configuration, "net10.0");
        foreach (var path in Directory.GetFiles(output)) File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
        var commands = new[] { "run", "hang", "helper", "crash-helper", "crash", "invalid-wire", "cancel" }.Select(id => new PluginCommand
        { Id = id, Title = id, Match = new() { Extensions = [".txt"] } }).ToArray();
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = "test.fixture", Name = "测试插件", Version = version, ApiVersion = apiVersion,
            Entry = "PluginTestFixture.dll", Commands = commands
        }, PluginProtocol.Json));
        var package = directory + ".mexplug";
        ZipFile.CreateFromDirectory(directory, package);
        return package;
    }
    public void Dispose()
    {
        Task.Run(async () => await Manager.DisposeAsync()).GetAwaiter().GetResult();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
    internal sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T fallback) => Get(key) is { } value ? JsonSerializer.Deserialize<T>(value)! : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public void Set<T>(string key, T value) => Set(key, JsonSerializer.Serialize(value));
        public Dictionary<string, string> GetAll() => new(_values);
    }
}

internal static class FileConversionTestExtensions
{
    public static async Task<FileConversionResult> ConvertAsync(this MacExplorer.Services.Impl.FileConversionService service,
        FileConversionRequest request, CancellationToken token = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "fkfinder-conversion-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var result = await service.ConvertAsync(request, work, token);
            var output = await PluginOutputCommitter.CommitOutputAsync(result.OutputPath, request.SourcePath,
                request.Format.ToString().ToLowerInvariant(), token);
            return result with { OutputPath = output };
        }
        finally { Directory.Delete(work, true); }
    }
}
