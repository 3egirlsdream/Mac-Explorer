using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace MacExplorer.PluginSdk;

// Helpers started by a plugin must be registered so pipe closure also reaps them.
public static class PluginChildProcesses
{
    public static event Action<int, long>? Started;
    private static readonly ConcurrentDictionary<int, Process> Processes = new();
    public static IDisposable Track(Process process)
    {
        var pid = process.Id;
        var registration = new Registration(pid);
        Processes[pid] = process;
        try
        {
            if (Started is { } started && GetLiveStartTime(process) is { } startTime)
                started(pid, startTime);
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    private static long? GetLiveStartTime(Process process)
    {
        try
        {
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is (InvalidOperationException or Win32Exception) && process.HasExited)
        {
            // A short-lived helper can exit between the liveness check and macOS's process query.
            return null;
        }
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
