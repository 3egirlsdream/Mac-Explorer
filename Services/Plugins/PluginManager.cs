using MacExplorer.PluginSdk;
using MacExplorer.Services.Impl;

namespace MacExplorer.Services.Plugins;

public sealed record InstalledPlugin(PluginManifest Manifest, bool Enabled, bool Running, string? LastError)
{
    public string Status => !Enabled ? LocalizationText.Get("已禁用")
        : Running ? LocalizationText.Get("正在运行") : LocalizationText.Get("已启用");

    public string SupportedTypes => string.Join("、", Manifest.Commands.SelectMany(c =>
        c.Match.Extensions.Concat(c.Match.FileNames)
            .Concat(c.Match.TextFiles ? [LocalizationText.Get("文本文件")] : Array.Empty<string>())).Distinct());
}

public sealed class PluginManager : IAsyncDisposable
{
    public const string BuiltInId = "com.macexplorer.file-conversion";
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _builtInDirectory = Path.Combine(RuntimePaths.BundleResourceDirectory, "BuiltInConversion");
    private readonly string _workRoot;
    private InstalledPlugin[] _plugins = [];
    private PluginSession? _session;
    private bool _stopping;

    public PluginManager() : this(Path.Combine(RuntimePaths.DataDirectory, "Plugins", ".work")) { }
    internal PluginManager(string workRoot) => _workRoot = workRoot;

    public IReadOnlyList<InstalledPlugin> Plugins => Volatile.Read(ref _plugins);
    public event Action? Changed;

    internal static NotSupportedException ExternalPluginsUnavailable() =>
        new(LocalizationText.Get("plugin.nativeOnly"));

    public Task InstallAsync(string package, CancellationToken token = default) =>
        Task.FromException(ExternalPluginsUnavailable());

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (Plugins.Count != 0) return;
            try
            {
                var manifest = PluginPackage.ReadManifest(_builtInDirectory);
                if (manifest.Id != BuiltInId)
                    throw new InvalidDataException("内置插件标识不匹配。");
                Publish(new(manifest, true, false, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or System.Text.Json.JsonException)
            {
                Publish(new(new() { Id = BuiltInId, Name = "文件转换" }, false, false, ex.Message));
            }
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task<PluginSession> StartAsync(string id, string commandId, PluginFile[] files, CancellationToken token = default)
    {
        if (id != BuiltInId) throw ExternalPluginsUnavailable();
        await InitializeAsync(token);

        PluginSession session;
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var plugin = Plugins[0];
            if (_session != null)
                throw new InvalidOperationException("此插件正在处理另一项任务。");
            if (!plugin.Enabled || !plugin.Manifest.Commands.Any(c => c.Id == commandId && c.Match.Matches(files))
                || files.Any(f => !File.Exists(f.Path)))
                throw new InvalidOperationException("所选文件不存在或不适用于此插件命令。");

            var work = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
            _session = session = new PluginSession(plugin.Manifest, work, ReleaseAsync);
            Publish(plugin with { Running = true, LastError = null });
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        return session;
    }

    public async Task CancelAsync(string id)
    {
        await _gate.WaitAsync();
        try { if (id == BuiltInId) _session?.Cancel(); }
        finally { _gate.Release(); }
    }

    public async Task RecordErrorAsync(string id, string message)
    {
        await _gate.WaitAsync();
        try
        {
            if (id != BuiltInId || Plugins.Count == 0) return;
            Publish(Plugins[0] with { LastError = message });
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _session = null;
            Publish(Plugins[0] with { Running = false });
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private void Publish(InstalledPlugin plugin) => Volatile.Write(ref _plugins, [plugin]);

    public async ValueTask DisposeAsync()
    {
        PluginSession? session;
        await _gate.WaitAsync();
        try { _stopping = true; session = _session; }
        finally { _gate.Release(); }
        if (session != null) await session.DisposeAsync();
    }
}
