using System.Text.Json;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

public sealed record InstalledPlugin(PluginManifest Manifest, string Directory, bool Enabled, bool BuiltIn,
    bool Running, bool Removed, string? LastError, string? LogPath, bool FromMarket = false)
{
    public string Status => Removed ? (Running ? "等待任务结束后卸载" : "已卸载") : !Enabled ? "已禁用" : Running ? "正在运行" : "已启用";
    public string SupportedTypes => string.Join("、", Manifest.Commands.SelectMany(c =>
        c.Match.Extensions.Concat(c.Match.FileNames).Concat(c.Match.TextFiles ? new[] { "文本文件" } : [])).Distinct());
}

public sealed partial class PluginManager : IAsyncDisposable
{
    public const string BuiltInId = "com.macexplorer.file-conversion";
    private const string StateKey = "plugins_state_v1";
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, PluginState> _state;
    private readonly Dictionary<string, PluginSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _leasedDirectories = new(StringComparer.Ordinal);
    private readonly string _bundledPackage;
    private readonly string _executable;
    private readonly string _assembly;
    private InstalledPlugin[] _plugins = [];
    private bool _initialized;
    private bool _stopping;
    public string RootDirectory { get; }
    public IReadOnlyList<InstalledPlugin> Plugins => Volatile.Read(ref _plugins);
    public event Action? Changed;

    public PluginManager(ISettingsService settings) : this(settings,
        Environment.GetEnvironmentVariable("MACEXPLORER_PLUGIN_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacExplorer", "Plugins"),
        Path.Combine(AppContext.BaseDirectory, "BundledPlugins", "FileConversion.mexplug"),
        Environment.ProcessPath!, typeof(PluginManager).Assembly.Location) { }

    internal PluginManager(ISettingsService settings, string root, string bundledPackage, string executable, string assembly)
    {
        _settings = settings; RootDirectory = root; _bundledPackage = bundledPackage; _executable = executable; _assembly = assembly;
        try { _state = JsonSerializer.Deserialize<Dictionary<string, PluginState>>(settings.Get(StateKey) ?? "{}", PluginProtocol.Json) ?? new(); }
        catch (JsonException) { _state = new(); }
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(RootDirectory);
            if (File.Exists(_bundledPackage) && (!_state.TryGetValue(BuiltInId, out var existing) || !existing.Removed) && NeedsBundledUpdate())
            {
                try { await InstallCoreAsync(_bundledPackage, true, false, CancellationToken.None); }
                catch (Exception ex)
                {
                    if (!_state.TryGetValue(BuiltInId, out var state)) _state[BuiltInId] = state = new() { BuiltIn = true };
                    state.LastError = "内置插件安装失败：" + ex.Message;
                    Save();
                }
            }
            _initialized = true;
            Reload();
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private bool NeedsBundledUpdate()
    {
        if (!_state.TryGetValue(BuiltInId, out var installed) || !Directory.Exists(installed.Directory)) return true;
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(_bundledPackage);
            var entry = zip.GetEntry("plugin.json");
            if (entry == null) return true;
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<PluginManifest>(stream, PluginProtocol.Json);
            return manifest == null || !Version.TryParse(manifest.Version, out var bundledVersion) ||
                !Version.TryParse(installed.Version, out var installedVersion) || bundledVersion > installedVersion;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        { return true; }
    }

    public async Task InstallAsync(string package, CancellationToken token = default, bool fromMarket = false)
    {
        await _gate.WaitAsync(token);
        try { ThrowIfStopping(); await InstallCoreAsync(package, false, true, token, fromMarket); Reload(); }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task RestoreBuiltInAsync()
    {
        await _gate.WaitAsync();
        try { ThrowIfStopping(); await InstallCoreAsync(_bundledPackage, true, true, CancellationToken.None); Reload(); }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private async Task InstallCoreAsync(string package, bool builtIn, bool explicitInstall, CancellationToken token, bool fromMarket = false)
    {
        var staging = Path.Combine(RootDirectory, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await PluginPackage.ExtractAsync(package, staging, token);
            var manifest = PluginPackage.ReadManifest(staging);
            if (builtIn && manifest.Id != BuiltInId) throw new InvalidDataException("内置插件标识不匹配。");
            _state.TryGetValue(manifest.Id, out var previous);
            if (!explicitInstall && previous is { Removed: false } && Version.TryParse(previous.Version, out var version) &&
                version >= Version.Parse(manifest.Version) && Directory.Exists(previous.Directory)) return;
            var parent = Path.Combine(RootDirectory, manifest.Id);
            Directory.CreateDirectory(parent);
            var destination = Path.Combine(parent, manifest.Version + "-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, destination);
            _state[manifest.Id] = new()
            {
                Directory = destination, Version = manifest.Version, Name = manifest.Name,
                Enabled = explicitInstall || previous?.Enabled != false, BuiltIn = builtIn || previous?.BuiltIn == true,
                LogPath = previous?.LogPath, FromMarket = fromMarket
            };
            try { Save(); }
            catch
            {
                if (previous == null) _state.Remove(manifest.Id); else _state[manifest.Id] = previous;
                Directory.Delete(destination, true); throw;
            }
            CleanupVersions(manifest.Id);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            ThrowIfStopping();
            if (!_state.TryGetValue(id, out var state) || state.Removed) throw new InvalidOperationException("插件未安装。");
            state.Enabled = enabled; Save(); Reload();
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task UninstallAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            ThrowIfStopping();
            if (!_state.TryGetValue(id, out var state)) return;
            state.Removed = true; state.Enabled = false; Save(); CleanupVersions(id); Reload();
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public Task<PluginSession> StartAsync(string id, string commandId, PluginFile[] files, CancellationToken token = default)
        => StartCoreAsync(id, commandId, files, false, token);
    public Task<PluginSession> StartAccountAsync(string id, CancellationToken token = default)
        => StartCoreAsync(id, "", [], true, token);
    private async Task<PluginSession> StartCoreAsync(string id, string commandId, PluginFile[] files, bool account, CancellationToken token)
    {
        PluginSession session;
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfStopping();
            var plugin = Plugins.SingleOrDefault(p => p.Manifest.Id == id && !p.Removed && p.Enabled)
                ?? throw new InvalidOperationException("插件已禁用或卸载。");
            if (_sessions.ContainsKey(id)) throw new InvalidOperationException("此插件正在处理另一项任务。");
            if (account ? !plugin.Manifest.HasUserInterface : (!plugin.Manifest.Commands.Any(c => c.Id == commandId && c.Match.Matches(files)) || files.Any(f => !File.Exists(f.Path) || Directory.Exists(f.Path))))
                throw new InvalidOperationException("所选文件不存在或不适用于此插件命令。");
            var work = Path.Combine(RootDirectory, ".work", Guid.NewGuid().ToString("N"));
            var log = Path.Combine(RootDirectory, ".logs", id + ".log");
            _state[id].LogPath = log; _state[id].LastError = null; Save();
            try { session = new PluginSession(_executable, _assembly, plugin.Directory, work, log, () => ReleaseAsync(id)); }
            catch { if (Directory.Exists(work)) Directory.Delete(work, true); throw; }
            _sessions.Add(id, session); _leasedDirectories.Add(id, plugin.Directory);
            Reload();
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        try
        {
            var hello = await session.CallAsync<JsonElement>("initialize", null, TimeSpan.FromSeconds(30), token);
            if (hello.GetProperty("apiVersion").GetInt32() != PluginProtocol.ApiVersion) throw new InvalidDataException("插件运行协议不兼容。");
            return session;
        }
        catch (Exception ex) { await RecordErrorAsync(id, ex.Message); await session.DisposeAsync(); throw; }
    }

    public async Task CancelAsync(string id)
    {
        await _gate.WaitAsync();
        try { if (_sessions.TryGetValue(id, out var session)) session.Cancel(); }
        finally { _gate.Release(); }
    }

    public async Task RecordErrorAsync(string id, string message)
    {
        await _gate.WaitAsync();
        try { if (_state.TryGetValue(id, out var state)) { state.LastError = message; Save(); Reload(); } }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private async Task ReleaseAsync(string id)
    {
        await _gate.WaitAsync();
        try { _sessions.Remove(id); _leasedDirectories.Remove(id); CleanupVersions(id); Reload(); }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private void CleanupVersions(string id)
    {
        var parent = Path.Combine(RootDirectory, id);
        if (!Directory.Exists(parent)) return;
        foreach (var path in Directory.GetDirectories(parent))
        {
            if (_leasedDirectories.TryGetValue(id, out var leased) && leased == path) continue;
            if (_state.TryGetValue(id, out var state) && !state.Removed && state.Directory == path) continue;
            try { Directory.Delete(path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Reload()
    {
        var plugins = new List<InstalledPlugin>();
        foreach (var (id, state) in _state)
        {
            PluginManifest manifest;
            try { manifest = PluginPackage.ReadManifest(state.Directory); }
            catch (Exception ex)
            {
                manifest = new() { Id = id, Name = state.Name, Version = state.Version };
                if (!state.Removed) state.LastError = ex.Message;
            }
            plugins.Add(new(manifest, state.Directory, state.Enabled && manifest.Commands.Length > 0, state.BuiltIn,
                _sessions.ContainsKey(id), state.Removed, state.LastError, state.LogPath, state.FromMarket));
        }
        Volatile.Write(ref _plugins, plugins.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).ToArray());
    }

    private void Save() => _settings.Set(StateKey, JsonSerializer.Serialize(_state, PluginProtocol.Json));
    private void ThrowIfStopping() => ObjectDisposedException.ThrowIf(_stopping, this);

    public async ValueTask DisposeAsync()
    {
        PluginSession[] sessions;
        await _gate.WaitAsync();
        try { _stopping = true; sessions = _sessions.Values.ToArray(); }
        finally { _gate.Release(); }
        foreach (var session in sessions) session.Cancel();
        await Task.WhenAll(sessions.Select(s => s.DisposeAsync().AsTask()));
    }

    public sealed class PluginState
    {
        public string Directory { get; set; } = "";
        public string Version { get; set; } = "0.0.0";
        public string Name { get; set; } = "文件转换";
        public bool Enabled { get; set; } = true;
        public bool BuiltIn { get; set; }
        public bool FromMarket { get; set; }
        public bool Removed { get; set; }
        public string? LastError { get; set; }
        public string? LogPath { get; set; }
    }
}
