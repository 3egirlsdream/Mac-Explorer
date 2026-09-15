using System.Collections.Concurrent;
using System.Diagnostics;

namespace MacExplorer.PluginSdk;

// Helpers started by a plugin must be registered so pipe closure also reaps them.
public static class PluginChildProcesses
{
    public static event Action<int, long>? Started;
    private static readonly ConcurrentDictionary<int, Process> Processes = new();
    public static IDisposable Track(Process process)
    {
        Processes[process.Id] = process;
        Started?.Invoke(process.Id, process.StartTime.ToUniversalTime().Ticks);
        return new Registration(process.Id);
    }
    public static void KillAll()
    {
        foreach (var process in Processes.Values)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
    private sealed class Registration(int id) : IDisposable
    {
        public void Dispose() => Processes.TryRemove(id, out _);
    }
}
