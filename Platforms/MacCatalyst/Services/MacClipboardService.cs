using System.Diagnostics;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacClipboardService : IClipboardService
{
    private ClipboardEntry? _entry;

    public void CopyFiles(string[] paths)
    {
        SetClipboardEntry(paths, ClipboardOperation.Copy);
    }

    public void CutFiles(string[] paths)
    {
        SetClipboardEntry(paths, ClipboardOperation.Cut);
    }

    public async Task CopyTextAsync(string text)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/pbcopy")
        {
            CreateNoWindow = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法访问系统剪贴板");
        await process.StandardInput.WriteAsync(text);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("复制到系统剪贴板失败");
    }

    public Task PasteFilesAsync(string targetDirectory) => Task.CompletedTask;

    public bool HasClipboardFiles => _entry is { IsEmpty: false };

    public ClipboardEntry? GetClipboardEntry() => _entry;

    public void Clear()
    {
        _entry = null;
    }

    private void SetClipboardEntry(string[] paths, ClipboardOperation operation)
    {
        var existingPaths = paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _entry = new ClipboardEntry
        {
            SourcePaths = existingPaths.ToList(),
            Operation = operation
        };

        if (existingPaths.Length > 0)
            _ = WriteToSystemPasteboardAsync(existingPaths);
    }

    private async Task WriteToSystemPasteboardAsync(IReadOnlyList<string> paths)
    {
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            var items = paths
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .Select(path => new
                {
                    Path = path,
                    IsDirectory = Directory.Exists(path)
                })
                .ToArray();

            if (items.Length == 0) return;

            var script = $$"""
ObjC.import('AppKit');
ObjC.import('Foundation');

const items = {{JsonSerializer.Serialize(items)}};
const urls = $.NSMutableArray.array;
const filenames = $.NSMutableArray.array;
let firstUrlString = null;
for (const item of items) {
  const url = $.NSURL.fileURLWithPathIsDirectory(item.Path, item.IsDirectory);
  urls.addObject(url);
  filenames.addObject(item.Path);
  if (firstUrlString === null) {
    firstUrlString = ObjC.unwrap(url.absoluteString);
  }
}

const pasteboard = $.NSPasteboard.generalPasteboard;
pasteboard.clearContents;
const ok = pasteboard.writeObjects(urls);
if (!ok) {
  throw new Error('Failed to write file URLs to NSPasteboard');
}
pasteboard.setPropertyListForType(filenames, 'NSFilenamesPboardType');
if (firstUrlString !== null) {
  pasteboard.setStringForType(firstUrlString, 'NSURLPboardType');
  pasteboard.setStringForType(firstUrlString, 'Apple URL pasteboard type');
}
""";
            var startInfo = new ProcessStartInfo("/usr/bin/osascript")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("JavaScript");
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(script);

            using var process = Process.Start(startInfo);
            if (process != null)
                await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // The in-app clipboard remains valid even if macOS rejects pasteboard sync.
        }
    }
}
