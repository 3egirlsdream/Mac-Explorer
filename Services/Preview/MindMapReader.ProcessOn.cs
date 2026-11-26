using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MacExplorer.Services.Preview;

internal sealed partial class MindMapReader
{
    private static readonly string[] ProcessOnWrappers = ["diagram", "definition", "content", "data", "mind", "mindmap", "json", "elements"];
    private static readonly string[] ProcessOnSheets = ["sheets", "pages", "diagrams", "canvases"];
    private static readonly string[] ProcessOnRoots = ["root", "rootNode", "rootTopic", "topic", "center", "centralTopic"];
    private static readonly string[] ProcessOnChildren = ["children", "childNodes", "topics", "branches", "nodes"];
    private static readonly string[] ProcessOnFlatLists = ["nodes", "elements", "items", "cells"];

    private IReadOnlyList<MindMapSheet> ReadProcessOn(byte[] bytes)
    {
        using var json = ParseJson(bytes);
        var sheets = new List<MindMapSheet>();
        ReadProcessOnPayload(json.RootElement, sheets, title: null, declaredMindMap: false, depth: 0);
        return sheets;
    }

    private void ReadProcessOnPayload(JsonElement payload, List<MindMapSheet> sheets, string? title, bool declaredMindMap, int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 8) return;

        var metadata = Property(payload, "meta");
        var info = Property(metadata, "diagramInfo");
        var category = String(info, "category") ?? String(payload, "category");
        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalized = category.Trim().ToLowerInvariant();
            if (normalized is not ("mind" or "mindmap" or "mind-map" or "mind map")
                && !normalized.StartsWith("mind_", StringComparison.Ordinal))
                return;
            declaredMindMap = true;
        }
        if (String(payload, "type")?.ToLowerInvariant() is "flow" or "flowchart") return;
        declaredMindMap |= String(metadata, "type")?.Contains("mind", StringComparison.OrdinalIgnoreCase) == true
            || Boolean(payload, "root");

        title ??= String(info, "title") ?? String(payload, "title") ?? String(payload, "name");
        foreach (var key in ProcessOnSheets)
        {
            var collection = Property(payload, key);
            if (collection.ValueKind != JsonValueKind.Array || collection.GetArrayLength() == 0) continue;
            foreach (var sheet in Items(collection))
                ReadProcessOnPayload(sheet, sheets, null, declaredMindMap, depth + 1);
            return;
        }

        foreach (var key in ProcessOnRoots)
        {
            var root = Property(payload, key);
            if (root.ValueKind != JsonValueKind.Object || !IsProcessOnTree(root, explicitRoot: true)) continue;
            AddSheet(sheets, new MindMapSheet(title, [ReadProcessOnTree(root, 0)]));
            return;
        }

        var nodeList = payload.ValueKind == JsonValueKind.Array ? payload : FirstArray(payload, ProcessOnFlatLists);
        if (nodeList.ValueKind == JsonValueKind.Array && ReadProcessOnFlat(nodeList, declaredMindMap) is { Count: > 0 } flat)
        {
            AddSheet(sheets, new MindMapSheet(title, flat));
            return;
        }

        if (IsProcessOnTree(payload, declaredMindMap))
        {
            AddSheet(sheets, new MindMapSheet(title, [ReadProcessOnTree(payload, 0)]));
            return;
        }

        foreach (var key in ProcessOnWrappers)
        {
            var nested = Property(payload, key);
            var previousCount = sheets.Count;
            if (nested.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                ReadProcessOnPayload(nested, sheets, title, declaredMindMap, depth + 1);
            else if (nested.ValueKind == JsonValueKind.String && nested.GetString() is { } raw)
            {
                var value = raw.AsSpan().TrimStart();
                if (!value.IsEmpty && value[0] is '{' or '[')
                {
                    using var decoded = JsonDocument.Parse(raw, JsonOptions);
                    ReadProcessOnPayload(decoded.RootElement, sheets, title, declaredMindMap, depth + 1);
                }
            }
            if (sheets.Count > previousCount) return;
        }
    }

    private static bool IsProcessOnTree(JsonElement node, bool explicitRoot)
    {
        if (node.ValueKind != JsonValueKind.Object) return false;
        if (Property(node, "nodes").ValueKind == JsonValueKind.Array) return false;
        return TreeChildren(node).Any() || (explicitRoot && ProcessOnTitle(node).Length > 0);
    }

    private static IEnumerable<JsonElement> TreeChildren(JsonElement node)
    {
        return MainChildren(node).Concat(Items(Property(node, "leftChildren")));

        static IEnumerable<JsonElement> MainChildren(JsonElement node)
        {
            foreach (var key in ProcessOnChildren)
            {
                var children = Property(node, key);
                if (children.ValueKind == JsonValueKind.Array) return Items(children);
                if (children.ValueKind == JsonValueKind.Object)
                    return Items(Property(children, "attached")).Concat(Items(Property(children, "detached")))
                        .Concat(Items(Property(children, "children"))).Concat(Items(Property(children, "list")));
            }
            return [];
        }
    }

    private MindMapNode ReadProcessOnTree(JsonElement node, int depth)
    {
        VisitNode(depth);
        return new MindMapNode(ProcessOnTitle(node), TreeChildren(node).Where(child => child.ValueKind == JsonValueKind.Object)
            .Select(child => ReadProcessOnTree(child, depth + 1)).ToArray(), ProcessOnCollapsed(node));
    }

    private IReadOnlyList<MindMapNode>? ReadProcessOnFlat(JsonElement items, bool declaredMindMap)
    {
        if (items.GetArrayLength() > MaxNodes) throw new MindMapLimitException();
        var nodes = new Dictionary<string, (JsonElement Node, string? Parent)>(StringComparer.Ordinal);
        foreach (var item in Items(items))
        {
            token.ThrowIfCancellationRequested();
            if (Property(item, "edge").ValueKind == JsonValueKind.True || String(item, "type") is "edge" or "linker"
                || Property(item, "source").ValueKind != JsonValueKind.Undefined
                || Property(item, "target").ValueKind != JsonValueKind.Undefined
                || Property(item, "sourceId").ValueKind != JsonValueKind.Undefined
                || Property(item, "targetId").ValueKind != JsonValueKind.Undefined)
                continue;
            var id = Id(item, "id", "uuid", "key", "cellId");
            if (id == null) continue;
            var parent = Id(item, "parentId", "parent", "pid", "pId");
            if (!nodes.TryAdd(id, (item, parent))) return null;
        }

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var (id, value) in nodes)
        {
            if (value.Parent is not { } parent || !nodes.ContainsKey(parent))
            {
                roots.Add(id);
                continue;
            }
            if (!children.TryGetValue(parent, out var branch)) children[parent] = branch = [];
            branch.Add(id);
        }

        // Connector-only diagrams have no topic parent hierarchy. Without an
        // explicit mind map marker, require a single connected hierarchy.
        if (roots.Count == 0 || (!declaredMindMap && (roots.Count != 1 || children.Count == 0))) return null;
        var visited = 0;
        var result = roots.Select(id => Build(id, 0)).ToArray();
        return visited == nodes.Count ? result : null;

        MindMapNode Build(string id, int depth)
        {
            VisitNode(depth);
            visited++;
            var item = nodes[id].Node;
            var branches = children.TryGetValue(id, out var branch)
                ? branch.Select(child => Build(child, depth + 1)).ToArray() : [];
            return new MindMapNode(ProcessOnTitle(item), branches, ProcessOnCollapsed(item));
        }
    }

    private static bool ProcessOnCollapsed(JsonElement node) => Boolean(node, "collapsed") || Boolean(node, "folded")
        || Boolean(Property(node, "data"), "collapsed");

    private static string ProcessOnTitle(JsonElement node)
    {
        var data = Property(node, "data");
        var title = String(node, "title") ?? String(node, "text") ?? String(node, "label") ?? String(node, "value")
            ?? String(data, "title") ?? String(data, "text") ?? String(data, "label") ?? String(node, "name");
        if (title == null) return string.Empty;
        return LimitTitle(WebUtility.HtmlDecode(HtmlTag().Replace(HtmlBreak().Replace(LimitTitle(title), "\n"), "")));
    }

    private static string? Id(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Property(item, name);
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } id) return id;
        }
        return null;
    }

    private static JsonElement FirstArray(JsonElement element, string[] names)
    {
        foreach (var name in names)
        {
            var value = Property(element, name);
            if (value.ValueKind == JsonValueKind.Array) return value;
        }
        return default;
    }

    [GeneratedRegex(@"<(?:br\s*/?|/p|/div|/li)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex(@"</?(?:p|div|span|font|b|strong|i|em|u|s|li|ul|ol|a|img)(?:\s[^<>]*)?\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTag();
}
