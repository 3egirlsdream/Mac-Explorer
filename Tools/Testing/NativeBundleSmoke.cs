using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace MacExplorer.Tools.Testing;

internal static class NativeBundleSmoke
{
    public const string Argument = "--verify-native-bundle";

    public static async Task RunAsync()
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated test directory is required.");
        if (RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("The executable is running with a JIT runtime.");

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT sqlite_version()";
        using var image = new SKBitmap(2, 2);
        using var font = new HarfBuzzSharp.Buffer();
        font.AddUtf8("文件转换");

        foreach (var library in new[] { "libAvaloniaNative.dylib", "libraw.dylib", "libzip.dylib", "libMacExplorerNativeDrag.dylib" })
            NativeLibrary.Free(NativeLibrary.Load(Path.Combine(RuntimePaths.BundleFrameworkDirectory, library)));
        if (DistributionChannel.IsAppStore)
            NativeLibrary.Free(NativeLibrary.Load(Path.Combine(RuntimePaths.BundleFrameworkDirectory, "libMacExplorerStoreKit.dylib")));

        var work = Directory.CreateDirectory(RuntimePaths.DataDirectory).FullName;
        var source = Path.Combine(work, "conversion.md");
        await File.WriteAllTextAsync(source, "# NativeAOT 文件转换\n");
        await using var plugins = new PluginManager();
        var files = new[] { new PluginFile(source) };
        await using var session = await plugins.StartAsync(PluginManager.BuiltInId, "to-docx", files);
        var converted = await session.ExecuteAsync(new("verify", "to-docx", files, session.WorkDirectory), TimeSpan.FromSeconds(30), default);
        using var docx = ZipFile.OpenRead(converted.Outputs.Single().Path);
        if (docx.GetEntry("word/document.xml") is null)
            throw new InvalidDataException("The built-in converter did not produce a Word document.");

        if (CultureInfo.GetCultureInfo("zh-CN").CompareInfo.Compare("阿", "波") >= 0)
            throw new InvalidOperationException("Chinese collation is unavailable.");
        Console.WriteLine($"NativeAOT {DistributionChannel.IsAppStore}: SQLite {query.ExecuteScalar()}, Skia/HarfBuzz, DOCX, zh-CN; isolated at {root}");
    }
}
