namespace MacExplorer.Models;

public sealed record FileTagNode(FileTag Tag, IReadOnlyList<FileTagNode> Children)
{
    public bool HasFiles => Tag.ItemCount > 0 || Children.Any(child => child.HasFiles);
}

public static class FileTagTree
{
    public static IReadOnlyList<FileTagNode> Build(IEnumerable<FileTag> source, bool hideEmpty = false)
    {
        var tags = source.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var names = tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileTagNode? BuildNode(FileTag tag)
        {
            if (!visited.Add(tag.Name)) return null;
            var children = tags.Where(t => string.Equals(t.ParentName, tag.Name, StringComparison.OrdinalIgnoreCase))
                .Select(BuildNode).OfType<FileTagNode>().ToArray();
            return hideEmpty && tag.ItemCount == 0 && children.Length == 0 ? null : new FileTagNode(tag, children);
        }
        return tags.Where(t => t.ParentName == null || !names.Contains(t.ParentName))
            .Select(BuildNode).OfType<FileTagNode>().ToArray();
    }

    public static IEnumerable<FileTag> Descendants(IEnumerable<FileTag> source, string name)
    {
        var children = source.Where(t => t.ParentName != null).ToLookup(t => t.ParentName!, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var pending = new Stack<string>();
        pending.Push(name);
        while (pending.TryPop(out var parent))
            foreach (var child in children[parent])
                if (visited.Add(child.Name)) { yield return child; pending.Push(child.Name); }
    }
}
