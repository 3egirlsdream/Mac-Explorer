using System.Diagnostics;
using System.Runtime.InteropServices;
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
                if (_entry is { IsEmpty: false } && changeCount == _entryChangeCount)
                    return ClipboardPasteKind.InAppFiles;

                // A newer system clipboard (including plain text or empty content)
                // invalidates an old cut/copy operation; never fall back to stale files.
                _entry = null;
                _entryChangeCount = -1;
                if (changeCount != _probeChangeCount)
                {
                    _probeKind = ProbeExternalKind();
                    _probeChangeCount = changeCount;
                }

                return _probeKind;
            }
        }

        return _entry is { IsEmpty: false } ? ClipboardPasteKind.InAppFiles : ClipboardPasteKind.None;
    }

    public bool TryAdoptExternalFiles()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        var changeCount = CurrentChangeCount();
        if (_entry is { IsEmpty: false } && _entryChangeCount >= 0 && _entryChangeCount == changeCount)
            return false;

        try
        {
            var paths = ReadPasteboardFilePaths()
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0 || changeCount < 0 || changeCount != CurrentChangeCount()) return false;

            _entry = new ClipboardEntry
            {
                SourcePaths = paths.ToList(),
                Operation = ClipboardOperation.Copy
            };
            _entryChangeCount = changeCount;
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

            if (pasteboardType == PasteboardTiff)
            {
                // Do not allocate a second full managed TIFF buffer on the PNG path.
                var pngData = ConvertTiffToPng(data);
                var pngBytes = pngData == IntPtr.Zero ? [] : ReadDataBytes(pngData);
                if (pngBytes.Length > 0) return new ClipboardImageData(pngBytes, ".png");
            }
            var bytes = ReadDataBytes(data);
            return bytes.Length == 0 ? null : new ClipboardImageData(bytes, extension);
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
        _probeChangeCount = -1;
    }

    private void SetClipboardEntry(string[] paths, ClipboardOperation operation)
    {
        if (OperatingSystem.IsMacOS() && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => SetClipboardEntry(paths, operation)).GetAwaiter().GetResult();
            return;
        }
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
            WriteToSystemPasteboard(existingPaths);
    }

    private ClipboardPasteKind ProbeExternalKind()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pasteboard = GeneralPasteboard();
            if (pasteboard == IntPtr.Zero) return ClipboardPasteKind.None;
            var types = Send(pasteboard, "types");
            // File URLs win over an image representation of the same clipboard item.
            // Inspect advertised types only: opening a menu must not request image bytes.
            if (SendBool(types, "containsObject:", CreateString("public.file-url")) ||
                SendBool(types, "containsObject:", CreateString("NSFilenamesPboardType")))
                return ClipboardPasteKind.ExternalFiles;
            return TryFindImageType(pasteboard, out _, out _)
                ? ClipboardPasteKind.Image : ClipboardPasteKind.None;
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
        var types = Send(pasteboard, "types");
        foreach (var (type, ext) in ImagePasteboardTypes)
        {
            var typeString = CreateString(type);
            if (typeString == IntPtr.Zero) continue;
            if (!SendBool(types, "containsObject:", typeString)) continue;

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

    private void WriteToSystemPasteboard(IReadOnlyList<string> paths)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pasteboard = GeneralPasteboard();
            if (pasteboard == IntPtr.Zero) return;
            _entryChangeCount = CurrentChangeCount();
            var urls = Send(objc_getClass("NSMutableArray"), "array");
            var filenames = Send(objc_getClass("NSMutableArray"), "array");
            if (urls == IntPtr.Zero || filenames == IntPtr.Zero) return;
            IntPtr firstUrl = IntPtr.Zero;
            foreach (var path in paths)
            {
                var name = CreateString(path);
                var url = SendIntPtr(objc_getClass("NSURL"), "fileURLWithPath:", name);
                if (url == IntPtr.Zero) throw new IOException("无法创建剪贴板文件地址。");
                objc_msgSend_void_intptr(urls, sel_registerName("addObject:"), url);
                objc_msgSend_void_intptr(filenames, sel_registerName("addObject:"), name);
                if (firstUrl == IntPtr.Zero) firstUrl = url;
            }
            Send(pasteboard, "clearContents");
            if (!SendBool(pasteboard, "writeObjects:", urls))
                throw new IOException("无法将文件写入系统剪贴板。");
            SendBool(pasteboard, "setPropertyList:forType:", filenames, CreateString("NSFilenamesPboardType"));
            var text = CreateString(string.Join("\n", paths));
            foreach (var type in new[] { "public.utf8-plain-text", "public.plain-text", "public.text", "NSStringPboardType" })
                SendBool(pasteboard, "setString:forType:", text, CreateString(type));
            if (firstUrl != IntPtr.Zero)
            {
                var urlString = Send(firstUrl, "absoluteString");
                SendBool(pasteboard, "setString:forType:", urlString, CreateString("NSURLPboardType"));
                SendBool(pasteboard, "setString:forType:", urlString, CreateString("Apple URL pasteboard type"));
            }
            // The write and its ownership snapshot finish in the same UI operation.
            // No late osascript completion can adopt a newer clipboard as our own.
            _entryChangeCount = CurrentChangeCount();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("写入系统剪贴板失败：" + ex);
        }
        finally { objc_autoreleasePoolPop(pool); }
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

    private static bool SendBool(IntPtr receiver, string selector, IntPtr value)
        => receiver != IntPtr.Zero && value != IntPtr.Zero
            && objc_msgSend_bool_intptr(receiver, sel_registerName(selector), value) != 0;

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
    private static extern byte objc_msgSend_bool_intptr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_intptr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte objc_msgSend_bool_intptr_intptr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr value,
        IntPtr type);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlopen(string path, int mode);
}
