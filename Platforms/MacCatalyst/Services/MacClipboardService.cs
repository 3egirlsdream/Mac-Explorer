using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacClipboardService : IClipboardService
{
    private const string PasteboardPng = "public.png";
    private const string PasteboardTiff = "public.tiff";

    private static readonly (string Type, string Extension)[] ImagePasteboardTypes =
    [
        (PasteboardPng, ".png"),
        ("public.jpeg", ".jpg"),
        ("public.heic", ".heic"),
        ("com.compuserve.gif", ".gif"),
        (PasteboardTiff, ".tiff")
    ];

    private ClipboardEntry? _entry;
    private long _entryChangeCount = -1;
    private long _probeChangeCount = -1;
    private ClipboardPasteKind _probeKind = ClipboardPasteKind.None;
    private static bool _appKitLoaded;

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
        if (OperatingSystem.IsMacOS())
        {
            var copied = await Dispatcher.UIThread.InvokeAsync(() => TryWriteTextToPasteboard(text));
            if (copied)
                return;
        }

        await CopyTextWithPbcopyAsync(text).ConfigureAwait(false);
    }

    private static bool TryWriteTextToPasteboard(string text)
    {
        try
        {
            if (!EnsureAppKitLoaded())
                return false;

            var pasteboardClass = objc_getClass("NSPasteboard");
            if (pasteboardClass == IntPtr.Zero)
                return false;

            var pasteboard = Send(pasteboardClass, "generalPasteboard");
            if (pasteboard == IntPtr.Zero)
                return false;

            Send(pasteboard, "clearContents");
            var value = CreateString(text);
            var textTypes = new[]
            {
                "public.utf8-plain-text",
                "public.plain-text",
                "public.text",
                "NSStringPboardType"
            };

            var copied = false;
            foreach (var type in textTypes)
                copied |= SendBool(pasteboard, "setString:forType:", value, CreateString(type));

            return copied;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CopyTextWithPbcopyAsync(string text)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/pbcopy")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法访问系统剪贴板");

        await process.StandardInput.WriteAsync(text);
        process.StandardInput.Close();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(error)
                ? "复制到系统剪贴板失败"
                : $"复制到系统剪贴板失败：{error}";
            throw new InvalidOperationException(message);
        }
    }

    public Task PasteFilesAsync(string targetDirectory) => Task.CompletedTask;

    public bool HasClipboardFiles => _entry is { IsEmpty: false };

    public bool HasPasteableContent => GetPasteKind() != ClipboardPasteKind.None;

    public ClipboardPasteKind GetPasteKind()
    {
        if (OperatingSystem.IsMacOS())
        {
            var changeCount = CurrentChangeCount();
            if (changeCount >= 0)
            {
                // 内部条目写入进行中或未被他人改写时，以内部条目为准
                if (_entry is { IsEmpty: false } && (_entryChangeCount < 0 || changeCount == _entryChangeCount))
                    return ClipboardPasteKind.InAppFiles;

                if (changeCount != _probeChangeCount)
                {
                    _probeKind = ProbeExternalKind();
                    _probeChangeCount = changeCount;
                }

                if (_probeKind != ClipboardPasteKind.None)
                    return _probeKind;
            }
        }

        return _entry is { IsEmpty: false } ? ClipboardPasteKind.InAppFiles : ClipboardPasteKind.None;
    }

    public bool TryAdoptExternalFiles()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (_entry is { IsEmpty: false } && _entryChangeCount >= 0 && _entryChangeCount == CurrentChangeCount())
            return false;

        try
        {
            var paths = ReadPasteboardFilePaths()
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0) return false;

            _entry = new ClipboardEntry
            {
                SourcePaths = paths.ToList(),
                Operation = ClipboardOperation.Copy
            };
            _entryChangeCount = CurrentChangeCount();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public ClipboardImageData? ReadExternalImage()
    {
        if (!OperatingSystem.IsMacOS()) return null;

        var pool = objc_autoreleasePoolPush();
        try
        {
            var pasteboard = GeneralPasteboard();
            if (pasteboard == IntPtr.Zero) return null;
            if (!TryFindImageType(pasteboard, out var pasteboardType, out var extension)) return null;

            var data = SendIntPtr(pasteboard, "dataForType:", CreateString(pasteboardType));
            if (data == IntPtr.Zero) return null;

            var bytes = ReadDataBytes(data);
            if (bytes.Length == 0) return null;

            if (pasteboardType != PasteboardTiff)
                return new ClipboardImageData(bytes, extension);

            // TIFF 优先转成 PNG；转换失败时保留原始 TIFF，绝不丢图
            var pngData = ConvertTiffToPng(data);
            var pngBytes = pngData == IntPtr.Zero ? [] : ReadDataBytes(pngData);
            return pngBytes.Length > 0
                ? new ClipboardImageData(pngBytes, ".png")
                : new ClipboardImageData(bytes, ".tiff");
        }
        catch
        {
            return null;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public ClipboardEntry? GetClipboardEntry() => _entry;

    public void Clear()
    {
        _entry = null;
        _entryChangeCount = -1;
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
        _entryChangeCount = -1;

        if (existingPaths.Length > 0)
            _ = WriteToSystemPasteboardAsync(existingPaths);
    }

    private ClipboardPasteKind ProbeExternalKind()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pasteboard = GeneralPasteboard();
            if (pasteboard == IntPtr.Zero) return ClipboardPasteKind.None;
            if (TryFindImageType(pasteboard, out _, out _)) return ClipboardPasteKind.Image;
            return ReadPasteboardFilePaths().Count > 0
                ? ClipboardPasteKind.ExternalFiles
                : ClipboardPasteKind.None;
        }
        catch
        {
            return ClipboardPasteKind.None;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private long CurrentChangeCount()
    {
        var pasteboard = GeneralPasteboard();
        return pasteboard == IntPtr.Zero ? -1 : objc_msgSend_long(pasteboard, sel_registerName("changeCount"));
    }

    private static IntPtr GeneralPasteboard()
    {
        if (!EnsureAppKitLoaded()) return IntPtr.Zero;
        var pasteboardClass = objc_getClass("NSPasteboard");
        return pasteboardClass == IntPtr.Zero ? IntPtr.Zero : Send(pasteboardClass, "generalPasteboard");
    }

    private static bool TryFindImageType(IntPtr pasteboard, out string pasteboardType, out string extension)
    {
        foreach (var (type, ext) in ImagePasteboardTypes)
        {
            var typeString = CreateString(type);
            if (typeString == IntPtr.Zero) continue;
            if (SendIntPtr(pasteboard, "dataForType:", typeString) == IntPtr.Zero) continue;

            pasteboardType = type;
            extension = ext;
            return true;
        }

        pasteboardType = "";
        extension = "";
        return false;
    }

    private static List<string> ReadPasteboardFilePaths()
    {
        var pasteboard = GeneralPasteboard();
        if (pasteboard == IntPtr.Zero) return [];

        var paths = new List<string>();
        var items = Send(pasteboard, "pasteboardItems");
        if (items != IntPtr.Zero)
        {
            var count = (int)objc_msgSend_long(items, sel_registerName("count"));
            for (var index = 0; index < count; index++)
            {
                var item = objc_msgSend_intptr_long(items, sel_registerName("objectAtIndex:"), index);
                if (item == IntPtr.Zero) continue;

                var url = SendIntPtr(item, "stringForType:", CreateString("public.file-url"));
                var path = ToLocalFilePath(url);
                if (path != null) paths.Add(path);
            }
        }

        if (paths.Count == 0)
            paths.AddRange(ReadLegacyFileNames(pasteboard));

        return paths;
    }

    private static IEnumerable<string> ReadLegacyFileNames(IntPtr pasteboard)
    {
        var array = SendIntPtr(pasteboard, "propertyListForType:", CreateString("NSFilenamesPboardType"));
        if (array == IntPtr.Zero) yield break;

        var count = (int)objc_msgSend_long(array, sel_registerName("count"));
        for (var index = 0; index < count; index++)
        {
            var value = objc_msgSend_intptr_long(array, sel_registerName("objectAtIndex:"), index);
            var text = ToUtf8String(value);
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    private static string? ToLocalFilePath(IntPtr nsString)
    {
        var text = ToUtf8String(nsString);
        if (string.IsNullOrEmpty(text)) return null;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    }

    private static string? ToUtf8String(IntPtr nsString)
        => nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    private static byte[] ReadDataBytes(IntPtr data)
    {
        var length = objc_msgSend_long(data, sel_registerName("length"));
        if (length <= 0 || length > int.MaxValue) return [];

        var pointer = Send(data, "bytes");
        if (pointer == IntPtr.Zero) return [];

        var buffer = new byte[length];
        Marshal.Copy(pointer, buffer, 0, (int)length);
        return buffer;
    }

    private static IntPtr ConvertTiffToPng(IntPtr tiffData)
    {
        var repClass = objc_getClass("NSBitmapImageRep");
        var dictionaryClass = objc_getClass("NSDictionary");
        if (repClass == IntPtr.Zero || dictionaryClass == IntPtr.Zero) return IntPtr.Zero;

        var rep = SendIntPtr(repClass, "imageRepWithData:", tiffData);
        var properties = Send(dictionaryClass, "dictionary");
        if (rep == IntPtr.Zero || properties == IntPtr.Zero) return IntPtr.Zero;

        // NSBitmapImageFileTypePNG == 4
        return objc_msgSend_intptr_long_intptr(rep, sel_registerName("representationUsingType:properties:"), 4, properties);
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
const plainText = items.map(item => item.Path).join('\n');
pasteboard.setStringForType(plainText, 'public.utf8-plain-text');
pasteboard.setStringForType(plainText, 'public.plain-text');
pasteboard.setStringForType(plainText, 'public.text');
pasteboard.setStringForType(plainText, 'NSStringPboardType');
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
        finally
        {
            // 记录写入后的 changeCount，用于识别内部条目是否已被其他应用覆盖。
            await Dispatcher.UIThread.InvokeAsync(() => _entryChangeCount = CurrentChangeCount());
        }
    }

    private static bool EnsureAppKitLoaded()
    {
        if (_appKitLoaded) return true;

        try
        {
            _appKitLoaded = dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 1) != IntPtr.Zero;
        }
        catch
        {
            _appKitLoaded = false;
        }

        return _appKitLoaded;
    }

    private static IntPtr CreateString(string value)
    {
        var stringClass = objc_getClass("NSString");
        return stringClass == IntPtr.Zero
            ? IntPtr.Zero
            : objc_msgSend_string(stringClass, sel_registerName("stringWithUTF8String:"), value);
    }

    private static IntPtr Send(IntPtr receiver, string selector)
        => receiver == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(receiver, sel_registerName(selector));

    private static IntPtr SendIntPtr(IntPtr receiver, string selector, IntPtr argument)
        => receiver == IntPtr.Zero || argument == IntPtr.Zero
            ? IntPtr.Zero
            : objc_msgSend_intptr_intptr(receiver, sel_registerName(selector), argument);

    private static bool SendBool(IntPtr receiver, string selector, IntPtr value, IntPtr type)
        => receiver != IntPtr.Zero
            && value != IntPtr.Zero
            && type != IntPtr.Zero
            && objc_msgSend_bool_intptr_intptr(receiver, sel_registerName(selector), value, type) != 0;

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_string(
        IntPtr receiver,
        IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_intptr_intptr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long objc_msgSend_long(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_intptr_long(
        IntPtr receiver,
        IntPtr selector,
        long argument);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_intptr_long_intptr(
        IntPtr receiver,
        IntPtr selector,
        long first,
        IntPtr second);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte objc_msgSend_bool_intptr_intptr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr value,
        IntPtr type);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlopen(string path, int mode);
}
