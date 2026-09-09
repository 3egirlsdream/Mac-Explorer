using System.Diagnostics;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public sealed class MacFinderTagQueryService : IFinderTagQueryService
{
    private readonly ILogger<MacFinderTagQueryService>? _logger;

    public MacFinderTagQueryService(ILogger<MacFinderTagQueryService>? logger = null)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> FindFilePathsAsync(
        FileTag tag,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/mdfind"))
            return [];

        var aliases = tag.IsFinderColor
            ? FileTagCatalog.GetFinderAliases(tag.Name)
            : [FileTagCatalog.NormalizeName(tag.Name)];
        var query = string.Join(" || ", aliases.Select(alias =>
            $"kMDItemUserTags == \"{EscapeQueryValue(alias)}\"cd"));

        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/mdfind",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-0");
        startInfo.ArgumentList.Add(query);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                _logger?.LogDebug("mdfind exited with {ExitCode}: {Error}", process.ExitCode, error.Trim());
                throw new IOException($"Finder 标签查询失败：{error.Trim()}");
            }

            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unable to query Finder tag {Tag}", tag.Name);
            throw new IOException("无法查询 Finder 标签", ex);
        }
    }

    private static string EscapeQueryValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
