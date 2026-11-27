using System.Text.Json;
using MacExplorer.Services;
using MacExplorer.Services.Plugins;

namespace MacExplorer.Tests;

internal sealed class PluginTestEnvironment : IDisposable
{
    public static string Repository
    {
        get
        {
            if (Environment.GetEnvironmentVariable("MACEXPLORER_TEST_REPOSITORY") is { } repository) return repository;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "MacExplorer.csproj"))) root = root.Parent;
            return root?.FullName ?? throw new DirectoryNotFoundException("Repository not found.");
        }
    }
    public static string Configuration => AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";
    public static string ApplicationOutput => Environment.GetEnvironmentVariable("MACEXPLORER_TEST_APP_OUTPUT") ?? Path.Combine(Repository, "bin", Configuration, "net10.0", "osx-arm64");
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fkfinder-plugin-test-" + Guid.NewGuid().ToString("N"));
    public PluginManager Manager { get; }
    public PluginTestEnvironment()
    {
        Directory.CreateDirectory(Root);
        Manager = new PluginManager(Path.Combine(Root, ".work"));
        Task.Run(() => Manager.InitializeAsync()).GetAwaiter().GetResult();
    }
    public string Write(string name, string contents)
    {
        var path = Path.Combine(Root, name); File.WriteAllText(path, contents); return path;
    }
    public void Dispose()
    {
        Task.Run(async () => await Manager.DisposeAsync()).GetAwaiter().GetResult();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
    internal sealed class MemorySettings : ISettingsService
    {
        public event Action<string>? SettingChanged { add { } remove { } }
        private readonly Dictionary<string, string> _values = new();
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T fallback) => Get(key) is { } value
            ? typeof(T) == typeof(string) ? (T)(object)value : JsonSerializer.Deserialize<T>(value)! : fallback;
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
