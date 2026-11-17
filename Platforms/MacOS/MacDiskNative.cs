using System.Runtime.InteropServices;
using System.Text.Json;
using MacExplorer.Models;

namespace MacExplorer.Platforms.MacOS;

internal static class MacDiskNative
{
    private const string Library = "libMacExplorerNativeDrag.dylib";

    internal static IReadOnlyList<DiskInfo> GetMountedDisks()
    {
        var pointer = me_disk_information();
        try
        {
            if (pointer == IntPtr.Zero) throw new IOException(Services.Impl.LocalizationText.Get("disk.read-error"));
            return JsonSerializer.Deserialize<DiskInfo[]>(Marshal.PtrToStringUTF8(pointer)!) ?? [];
        }
        finally { me_string_free(pointer); }
    }

    [DllImport(Library)] private static extern IntPtr me_disk_information();
    [DllImport(Library)] private static extern void me_string_free(IntPtr pointer);
}
