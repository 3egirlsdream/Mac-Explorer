using MacExplorer.Services;
using MacExplorer.TextShaping;

namespace MacExplorer.Platforms.MacOS;

internal static class MacFontTableLifetime
{
    internal static void Prepare()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var installed = PohTextShaping.TryInstall();
        MemoryDiagnostics.Record("fonts.table_allocator", new { installed, allocator = "poh", avaloniaVersion = "12.0.4" });
    }
}
