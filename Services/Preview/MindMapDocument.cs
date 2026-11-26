using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;

namespace MacExplorer.Services.Preview;

internal sealed record MindMapNode(string Title, IReadOnlyList<MindMapNode> Children, bool IsCollapsed = false);
internal sealed record MindMapSheet(string? Title, IReadOnlyList<MindMapNode> Roots);
internal sealed record MindMapDocument(IReadOnlyList<MindMapSheet> Sheets);

internal sealed partial class MindMapReader(CancellationToken token)
{
    private const int MaxPayloadBytes = 16 * 1024 * 1024;
    private const int MaxSheets = 128;
    private const int MaxNodes = 5000;
    private const int MaxDepth = 96;
    private const int MaxTitleLength = 2048;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = MaxDepth * 3 + 16 };
    private int _nodeCount;

    internal static bool Supports(string extension) => extension is ".xmind" or ".mm" or ".opml" or ".mmap" or ".pos";

    // Non-map .mm files also use this extension (Objective-C++).
    internal enum Status { Ready, NotMindMap, Unavailable, TooLarge }

    internal static Task<(Status Status, MindMapDocument? Document)> ReadAsync(string path, string extension, CancellationToken token)
        => Task.Run(() => new MindMapReader(token).Read(path, extension), token);

    private (Status, MindMapDocument?) Read(string path, string extension)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (extension == ".mm" && !LooksLikeXml(file))
                return (Status.NotMindMap, null);

            IReadOnlyList<MindMapSheet> sheets = extension switch
            {
                ".xmind" or ".mmap" => ReadPackage(file, extension),
                ".pos" => ReadProcessOn(ReadPayload(file, file.Length)),
                _ => ReadXml(ReadPayload(file, file.Length), extension)
            };
            token.ThrowIfCancellationRequested();
            if (sheets.Count == 0)
                return (extension == ".mm" ? Status.NotMindMap : Status.Unavailable, null);
            return _nodeCount > 0 && sheets.Any(sheet => sheet.Roots.Any(HasTitle))
                ? (Status.Ready, new MindMapDocument(sheets))
                : (Status.Unavailable, null);
        }
        catch (MindMapLimitException) { return (Status.TooLarge, null); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                                   or NotSupportedException or XmlException or JsonException
                                   or SharpCompress.Common.ArchiveException or System.Security.Cryptography.CryptographicException)
        {
            return (Status.Unavailable, null);
        }
    }

    private static bool HasTitle(MindMapNode node) => node.Title.Length != 0 || node.Children.Any(HasTitle);

    private static bool LooksLikeXml(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        Span<char> buffer = stackalloc char[512];
        var count = reader.ReadBlock(buffer);
        stream.Position = 0;
        return buffer[..count].TrimStart().StartsWith("<", StringComparison.Ordinal);
    }

    private IReadOnlyList<MindMapSheet> ReadPackage(Stream file, string extension)
    {
        using var archive = ZipArchive.OpenArchive(file);
        var entries = new Dictionary<string, IArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Take(4096))
        {
            token.ThrowIfCancellationRequested();
            if (!entry.IsDirectory && entry.Key is { } key)
                entries.TryAdd(key, entry);
        }

        if (extension == ".xmind" && entries.TryGetValue("content.json", out var json))
            return ReadXMindJson(ReadEntry(json));

        var xmlName = extension == ".xmind" ? "content.xml" : "Document.xml";
        return entries.TryGetValue(xmlName, out var xml) ? ReadXml(ReadEntry(xml), extension) : [];
    }

    private byte[] ReadEntry(IArchiveEntry entry)
    {
        CheckPayloadSize(entry.Size);
        if (entry.IsEncrypted) throw new InvalidDataException();
        using var stream = entry.OpenEntryStream();
        return ReadPayload(stream, entry.Size);
    }

    private byte[] ReadPayload(Stream stream, long size)
    {
        CheckPayloadSize(size);
        var bytes = new byte[(int)size];
        for (var offset = 0; offset < bytes.Length; offset += 64 * 1024)
        {
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(bytes.AsSpan(offset, Math.Min(64 * 1024, bytes.Length - offset)));
        }
        return bytes;
    }

    private static void CheckPayloadSize(long size)
    {
        if (size > MaxPayloadBytes) throw new MindMapLimitException();
        if (size < 1) throw new InvalidDataException();
    }

    private IReadOnlyList<MindMapSheet> ReadXMindJson(byte[] bytes)
    {
        using var json = ParseJson(bytes);
        var sheets = new List<MindMapSheet>();
        foreach (var sheet in Items(json.RootElement))
        {
            var main = Property(sheet, "rootTopic");
            if (main.ValueKind != JsonValueKind.Object) continue;
            var roots = new List<MindMapNode>();
            var floating = new List<MindMapNode>();
            roots.Add(ReadXMindTopic(main, 0, floating));
            roots.AddRange(floating);
            AddSheet(sheets, new MindMapSheet(String(sheet, "title"), roots));
        }
        return sheets;
    }

    private MindMapNode ReadXMindTopic(JsonElement topic, int depth, List<MindMapNode> floating)
    {
        VisitNode(depth);
        var children = Property(topic, "children");
        var branches = Items(Property(children, "attached"))
            .Select(child => ReadXMindTopic(child, depth + 1, floating)).ToArray();
        foreach (var detached in Items(Property(children, "detached")))
            floating.Add(ReadXMindTopic(detached, depth + 1, floating));
        return new MindMapNode(LimitTitle(String(topic, "title")), branches,
            String(topic, "branch") == "folded" || Boolean(topic, "folded"));
    }

    private IReadOnlyList<MindMapSheet> ReadXml(byte[] bytes, string extension)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var xml = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPayloadBytes
        });
        var root = XDocument.Load(xml).Root;
        if (root == null) return [];

        switch (extension)
        {
            case ".xmind" when root.Name.LocalName == "xmap-content":
            {
                var sheets = new List<MindMapSheet>();
                foreach (var sheet in Elements(root, "sheet"))
                {
                    if (Child(sheet, "topic") is not { } topic) continue;
                    var roots = new List<MindMapNode>();
                    var floating = new List<MindMapNode>();
                    roots.Add(ReadTopic(topic, 0));
                    roots.AddRange(floating);
                    AddSheet(sheets, new MindMapSheet(Child(sheet, "title")?.Value, roots));

                    MindMapNode ReadTopic(XElement node, int depth)
                    {
                        VisitNode(depth);
                        var branches = new List<MindMapNode>();
                        foreach (var topics in Elements(Child(node, "children"), "topics"))
                        {
                            var destination = (string?)topics.Attribute("type") == "detached" ? floating : branches;
                            foreach (var child in Elements(topics, "topic"))
                                destination.Add(ReadTopic(child, depth + 1));
                        }
                        return new MindMapNode(LimitTitle(Child(node, "title")?.Value), branches,
                            (string?)node.Attribute("branch") == "folded");
                    }
                }
                return sheets;
            }
            case ".mm" when root.Name.LocalName == "map":
                return [new MindMapSheet(null, Elements(root, "node").Select(node =>
                    ReadXmlNode(node, "mm", 0)).ToArray())];
            case ".opml" when root.Name.LocalName == "opml":
            {
                var title = Child(Child(root, "head"), "title")?.Value;
                var roots = Elements(Child(root, "body"), "outline").Select(node => ReadXmlNode(node, "opml", 0)).ToArray();
                return [new MindMapSheet(LimitTitle(title), roots)];
            }
            case ".mmap" when root.Name.LocalName == "Map":
                return [new MindMapSheet(null, Elements(Child(root, "OneTopic"), "Topic")
                    .Select(node => ReadXmlNode(node, "mmap", 0)).ToArray())];
            default:
                return [];
        }
    }

    private MindMapNode ReadXmlNode(XElement node, string format, int depth)
    {
        VisitNode(depth);
        string? title;
        IEnumerable<XElement> children;
        var collapsed = false;
        switch (format)
        {
            case "mm":
                title = (string?)node.Attribute("TEXT") ?? (string?)node.Attribute("LOCALIZED_TEXT");
                if (string.IsNullOrWhiteSpace(title))
                {
                    var rich = Elements(node, "richcontent").FirstOrDefault(element => (string?)element.Attribute("TYPE") == "NODE");
                    var body = Child(Child(rich, "html"), "body") ?? rich;
                    if (body != null)
                        title = string.Join(" ", body.DescendantNodes().OfType<XText>()
                            .Select(part => part.Value.Trim()).Where(part => part.Length > 0));
                }
                collapsed = string.Equals((string?)node.Attribute("FOLDED"), "true", StringComparison.OrdinalIgnoreCase);
                children = Elements(node, "node");
                break;
            case "opml":
                title = (string?)node.Attribute("text") ?? (string?)node.Attribute("title");
                children = Elements(node, "outline");
                break;
            default:
                title = (string?)Child(node, "Text")?.Attribute("PlainText");
                collapsed = string.Equals((string?)node.Attribute("Collapsed"), "true", StringComparison.OrdinalIgnoreCase);
                children = Elements(Child(node, "SubTopics"), "Topic");
                break;
        }
        return new MindMapNode(LimitTitle(title), children.Select(child => ReadXmlNode(child, format, depth + 1)).ToArray(), collapsed);
    }

    private void VisitNode(int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > MaxDepth || ++_nodeCount > MaxNodes) throw new MindMapLimitException();
    }

    private static void AddSheet(List<MindMapSheet> sheets, MindMapSheet sheet)
    {
        if (sheets.Count >= MaxSheets) throw new MindMapLimitException();
        sheets.Add(sheet with { Title = LimitTitle(sheet.Title) });
    }

    private static string LimitTitle(string? title)
    {
        var trimmed = title.AsSpan().Trim();
        if (trimmed.Length <= MaxTitleLength) return trimmed.ToString();
        var end = char.IsHighSurrogate(trimmed[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
        return string.Concat(trimmed[..end], "…");
    }

    private static JsonDocument ParseJson(ReadOnlyMemory<byte> bytes)
        => JsonDocument.Parse(bytes.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes, JsonOptions);

    private static IEnumerable<XElement> Elements(XElement? element, string name)
        => element?.Elements().Where(child => child.Name.LocalName == name) ?? [];

    private static XElement? Child(XElement? element, string name)
        => Elements(element, name).FirstOrDefault();

    private static JsonElement Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static IEnumerable<JsonElement> Items(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    private static string? String(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool Boolean(JsonElement element, string name)
        => Property(element, name).ValueKind == JsonValueKind.True;

    private sealed class MindMapLimitException : Exception;
}
