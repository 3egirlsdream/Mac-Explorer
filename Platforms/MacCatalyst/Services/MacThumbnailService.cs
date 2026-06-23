using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacThumbnailService : IThumbnailService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp",
        ".heic", ".heif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf",
        ".rw2", ".pef", ".raw"
    };
    private static readonly HashSet<string> QuickLookDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".pages", ".numbers", ".key", ".rtf", ".odt", ".ods", ".odp"
    };

    private const int MaxMemoryEntries = 300;
    private const long MaxMemoryBytes = 64L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, byte[]> _memoryCache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();
    private readonly SemaphoreSlim _generationGate = new(1);
    private readonly string _diskCacheDirectory;
    private long _memoryBytes;

    public MacThumbnailService()
    {
        _diskCacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MacExplorer",
            "thumbnail-cache");
        Directory.CreateDirectory(_diskCacheDirectory);
    }

    public bool IsImageFile(string extension) =>
        !string.IsNullOrWhiteSpace(extension) && ImageExtensions.Contains(extension);

    public async Task<byte[]?> GetThumbnailAsync(
        string filePath,
        int maxPixelSize,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(filePath);
        if (!File.Exists(filePath) || (!IsImageFile(extension) && !QuickLookDocumentExtensions.Contains(extension)))
            return null;

        var cacheKey = $"{filePath}:{File.GetLastWriteTimeUtc(filePath).Ticks}:{maxPixelSize}";
        if (_memoryCache.TryGetValue(cacheKey, out var memoryBytes))
            return memoryBytes;

        var cachePath = GetCachePath(cacheKey);
        if (File.Exists(cachePath))
        {
            try
            {
                var diskBytes = await File.ReadAllBytesAsync(cachePath, ct);
                AddToMemory(cacheKey, diskBytes);
                return diskBytes;
            }
            catch
            {
                TryDelete(cachePath);
            }
        }

        await _generationGate.WaitAsync(ct);
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = await File.ReadAllBytesAsync(cachePath, ct);
                AddToMemory(cacheKey, cached);
                return cached;
            }

            var generated = await GenerateThumbnailAsync(filePath, cachePath, maxPixelSize, ct);
            if (generated == null) return null;
            AddToMemory(cacheKey, generated);
            return generated;
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public async Task<byte[]?> GetFaceCropAsync(
        string filePath,
        float bx,
        float by,
        float bw,
        float bh,
        int maxPixelSize = 128,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath) || bw <= 0 || bh <= 0)
            return null;

        var cacheKey = $"face:{filePath}:{File.GetLastWriteTimeUtc(filePath).Ticks}:{bx:F4}:{by:F4}:{bw:F4}:{bh:F4}:{maxPixelSize}";
        if (_memoryCache.TryGetValue(cacheKey, out var memoryBytes))
            return memoryBytes;

        var cachePath = GetCachePath(cacheKey);
        if (File.Exists(cachePath))
        {
            try
            {
                var diskBytes = await File.ReadAllBytesAsync(cachePath, ct);
                AddToMemory(cacheKey, diskBytes);
                return diskBytes;
            }
            catch
            {
                TryDelete(cachePath);
            }
        }

        await _generationGate.WaitAsync(ct);
        try
        {
            var dimensions = await GetDimensionsAsync(filePath, ct);
            if (dimensions == null) return null;

            var (width, height) = dimensions.Value;
            var cropWidth = Math.Max(1, (int)Math.Round(bw * width * 1.6));
            var cropHeight = Math.Max(1, (int)Math.Round(bh * height * 1.6));
            var centerX = (bx + bw / 2f) * width;
            var centerY = (1f - by - bh / 2f) * height;
            var offsetX = Math.Clamp((int)Math.Round(centerX - cropWidth / 2f), 0, Math.Max(0, width - cropWidth));
            var offsetY = Math.Clamp((int)Math.Round(centerY - cropHeight / 2f), 0, Math.Max(0, height - cropHeight));

            var arguments = new[]
            {
                "-c", cropHeight.ToString(), cropWidth.ToString(),
                "--cropOffset", offsetY.ToString(), offsetX.ToString(),
                "-Z", maxPixelSize.ToString(),
                "--setProperty", "format", "png",
                filePath, "--out", cachePath
            };
            if (!await RunSipsAsync(arguments, ct) || !File.Exists(cachePath))
                return null;

            var bytes = await File.ReadAllBytesAsync(cachePath, ct);
            AddToMemory(cacheKey, bytes);
            return bytes;
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public void EvictFromCache(string filePath)
    {
        foreach (var key in _memoryCache.Keys.Where(key => key.Contains(filePath, StringComparison.Ordinal)))
            RemoveFromMemory(key);
    }

    public void ClearCache()
    {
        _memoryCache.Clear();
        Interlocked.Exchange(ref _memoryBytes, 0);
        while (_cacheOrder.TryDequeue(out _)) { }
    }

    private async Task<byte[]?> GenerateThumbnailAsync(
        string sourcePath,
        string cachePath,
        int maxPixelSize,
        CancellationToken ct)
    {
        if (!IsImageFile(Path.GetExtension(sourcePath)))
            return await GenerateQuickLookThumbnailAsync(sourcePath, cachePath, maxPixelSize, ct);

        var arguments = new[]
        {
            "-Z", Math.Max(32, maxPixelSize).ToString(),
            "--setProperty", "format", "png",
            sourcePath, "--out", cachePath
        };
        if (await RunSipsAsync(arguments, ct) && File.Exists(cachePath))
            return await File.ReadAllBytesAsync(cachePath, ct);

        var info = new FileInfo(sourcePath);
        return info.Length <= 10 * 1024 * 1024
            ? await File.ReadAllBytesAsync(sourcePath, ct)
            : null;
    }

    private async Task<byte[]?> GenerateQuickLookThumbnailAsync(
        string sourcePath,
        string cachePath,
        int maxPixelSize,
        CancellationToken ct)
    {
        var outputDirectory = Path.Combine(_diskCacheDirectory, ".quicklook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/qlmanage",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-t", "-s", Math.Max(128, maxPixelSize).ToString(), "-o", outputDirectory, sourcePath })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process == null) return null;
            TrySetBelowNormalPriority(process);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }

            if (process.ExitCode != 0) return null;
            var generatedPath = Directory.EnumerateFiles(outputDirectory, "*.png").FirstOrDefault();
            if (generatedPath == null) return null;
            File.Move(generatedPath, cachePath, overwrite: true);
            return await File.ReadAllBytesAsync(cachePath, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); }
            catch { }
        }
    }

    private static async Task<(int Width, int Height)?> GetDimensionsAsync(
        string filePath,
        CancellationToken ct)
    {
        var startInfo = CreateSipsStartInfo(["-g", "pixelWidth", "-g", "pixelHeight", filePath]);
        using var process = Process.Start(startInfo);
        if (process == null) return null;
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var output = await outputTask;
        if (process.ExitCode != 0) return null;

        int width = 0, height = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0] == "pixelWidth") int.TryParse(parts[1], out width);
            if (parts[0] == "pixelHeight") int.TryParse(parts[1], out height);
        }
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static async Task<bool> RunSipsAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            process = Process.Start(CreateSipsStartInfo(arguments));
            if (process == null) return false;
            TrySetBelowNormalPriority(process);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            if (process != null)
                TryKillProcess(process);
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static ProcessStartInfo CreateSipsStartInfo(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/sips",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static void TrySetBelowNormalPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
        }
    }

    private string GetCachePath(string cacheKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
        return Path.Combine(_diskCacheDirectory, hash + ".png");
    }

    private void AddToMemory(string key, byte[] bytes)
    {
        if (!_memoryCache.TryAdd(key, bytes)) return;
        _cacheOrder.Enqueue(key);
        Interlocked.Add(ref _memoryBytes, bytes.LongLength);

        while (_memoryCache.Count > MaxMemoryEntries || Interlocked.Read(ref _memoryBytes) > MaxMemoryBytes)
        {
            if (!_cacheOrder.TryDequeue(out var oldest)) break;
            RemoveFromMemory(oldest);
        }
    }

    private void RemoveFromMemory(string key)
    {
        if (_memoryCache.TryRemove(key, out var removed))
            Interlocked.Add(ref _memoryBytes, -removed.LongLength);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
