using Avalonia;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

internal static class PluginWorker
{
    public static int Run(string directory)
    {
        if (!PluginPackage.ReadManifest(directory).HasUserInterface)
            return RunAsync(directory).GetAwaiter().GetResult();
        WorkerApplication.Directory = directory;
        return Avalonia.AppBuilder.Configure<WorkerApplication>().UsePlatformDetect()
            .StartWithClassicDesktopLifetime([], Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    private sealed class WorkerApplication : Avalonia.Application
    {
        public static string Directory = "";
        public override void Initialize()
        {
            Name = PluginPackage.ReadManifest(Directory).Name;
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        }
        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            _ = Task.Run(async () =>
            {
                var result = await RunAsync(Directory);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown(result));
            });
        }
    }

    public static async Task<int> RunAsync(string directory)
    {
        var wire = Console.Out;
        Console.SetOut(Console.Error);
        using var lifetime = new CancellationTokenSource();
        using var writes = new SemaphoreSlim(1);
        async Task SendAsync(PluginRpcMessage message)
        {
            await writes.WaitAsync();
            try { await wire.WriteLineAsync(JsonSerializer.Serialize(message, PluginProtocol.Json)); await wire.FlushAsync(); }
            finally { writes.Release(); }
        }
        PluginChildProcesses.Started += (pid, started) => SendAsync(new()
        {
            Method = "child-process", Params = JsonSerializer.SerializeToElement(new { pid, started }, PluginProtocol.Json)
        }).GetAwaiter().GetResult();
        Task active = Task.CompletedTask;
        try
        {
            var manifest = PluginPackage.ReadManifest(directory);
            var entry = Path.Combine(directory, manifest.Entry);
            var context = new PluginLoadContext(entry);
            var assembly = context.LoadFromAssemblyPath(entry);
            var type = assembly.GetTypes().Single(t => !t.IsAbstract && typeof(IFileActionPlugin).IsAssignableFrom(t));
            var plugin = (IFileActionPlugin)Activator.CreateInstance(type)!;
            async Task HandleAsync(PluginRpcMessage message)
            {
                try
                {
                    object result;
                    if (message.Method == "initialize") result = new { apiVersion = PluginProtocol.ApiVersion, processId = Environment.ProcessId };
                    else if (message.Method == "check-access")
                    {
                        var request = message.Params!.Value.Deserialize<PluginAccessRequest>(PluginProtocol.Json)!;
                        result = plugin is IPluginAccessProvider access
                            ? await access.CheckAccessAsync(request, lifetime.Token)
                            : manifest.Paid ? new PluginAccessResult(PluginAccessStatus.Failed, "付费插件缺少授权接口。")
                            : new PluginAccessResult(PluginAccessStatus.Allowed);
                    }
                    else if (message.Method == "account")
                    {
                        if (!manifest.HasUserInterface || plugin is not IPluginAccessProvider access)
                            throw new InvalidOperationException("插件不支持账号窗口。");
                        result = await access.ShowAccountAsync(message.Params!.Value.Deserialize<PluginInteractionRequest>(PluginProtocol.Json)!, lifetime.Token);
                    }
                    else
                    {
                        var invocation = message.Params?.Deserialize<PluginInvocation>(PluginProtocol.Json)
                            ?? throw new InvalidDataException("缺少调用参数。");
                        if (message.Method == "prepare") result = await plugin.PrepareAsync(invocation, lifetime.Token);
                        else if (message.Method == "execute")
                            result = await plugin.ExecuteAsync(invocation, new InlineProgress(value =>
                                SendAsync(new() { Method = "progress", Params = JsonSerializer.SerializeToElement(value, PluginProtocol.Json) }).GetAwaiter().GetResult()), lifetime.Token);
                        else throw new InvalidOperationException("未知插件方法。");
                    }
                    await SendAsync(new() { Id = message.Id, Result = JsonSerializer.SerializeToElement(result, PluginProtocol.Json) });
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(ex.ToString());
                    await SendAsync(new() { Id = message.Id, Error = new(ex is OperationCanceledException ? -32800 : -32000, ex.Message, ex.GetType().Name) });
                }
            }
            while (await Console.In.ReadLineAsync() is { } line)
            {
                var message = JsonSerializer.Deserialize<PluginRpcMessage>(line, PluginProtocol.Json)
                    ?? throw new InvalidDataException("无效插件消息。");
                if (message.Jsonrpc != "2.0") throw new InvalidDataException("不支持的通信版本。");
                if (message.Method == "cancel") { lifetime.Cancel(); continue; }
                if (message.Id == null) continue;
                await active;
                active = Task.Run(() => HandleAsync(message));
            }
            return 0;
        }
        catch (Exception ex) { await Console.Error.WriteLineAsync(ex.ToString()); return 1; }
        finally
        {
            lifetime.Cancel();
            PluginChildProcesses.KillAll();
            try { await active.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        }
    }

    private sealed class InlineProgress(Action<PluginProgress> report) : IProgress<PluginProgress>
    {
        public void Report(PluginProgress value) => report(value);
    }

    private sealed class PluginLoadContext(string entry) : AssemblyLoadContext("FileActionPlugin")
    {
        private readonly AssemblyDependencyResolver _resolver = new(entry);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "MacExplorer.PluginUi" || name.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
                return AssemblyLoadContext.Default.LoadFromAssemblyName(name);
            if (name.Name == typeof(IFileActionPlugin).Assembly.GetName().Name) return typeof(IFileActionPlugin).Assembly;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
