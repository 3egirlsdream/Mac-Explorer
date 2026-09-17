using System.Text;

namespace MacExplorer.Services.Search;

/// <summary>Literal, Unicode-normalized matching shared by the index and live enumeration.</summary>
public sealed record SearchQuery(
    IReadOnlyList<string> NameTerms,
    IReadOnlyList<string> PathTerms,
    IReadOnlyList<string> Extensions)
{
    public bool IsEmpty => NameTerms.Count + PathTerms.Count + Extensions.Count == 0;

    // Normalize search keys, NEVER filesystem identities. NFC/NFD filenames may refer
    // to distinct directory entries; the original path is retained throughout.
    public static string Fold(string value) => value.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    public static SearchQuery Parse(string input)
    {
        var names = new List<string>();
        var paths = new List<string>();
        var extensions = new List<string>();
        foreach (var token in Tokenize(input))
        {
            if (token.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            {
                extensions.AddRange(token[4..].Split(',', StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries).Select(e => Fold(e.TrimStart('.'))));
            }
            else if (token.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                if (token.Length > 5) paths.Add(Fold(token[5..]));
            }
            else if (token.Length > 0) names.Add(Fold(token));
        }
        return new(names.Distinct().ToArray(), paths.Distinct().ToArray(), extensions.Distinct().ToArray());
    }

    private static IEnumerable<string> Tokenize(string input)
    {
        var token = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\\' && i + 1 < input.Length && input[i + 1] is '"' or '\\')
                token.Append(input[++i]);
            else if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (token.Length > 0) { yield return token.ToString(); token.Clear(); }
            }
            else token.Append(c);
        }
        // An unfinished quote is valid while the user is still typing.
        if (token.Length > 0) yield return token.ToString();
    }

    public bool Matches(string name, string parentPath, string extension, string initials, bool usePinyin)
    {
        var nameKey = Fold(name);
        var parentKey = Fold(parentPath);
        return NameTerms.All(term => nameKey.Contains(term, StringComparison.Ordinal) ||
                   usePinyin && initials.Contains(term, StringComparison.Ordinal)) &&
               PathTerms.All(term => parentKey.Contains(term, StringComparison.Ordinal)) &&
               (Extensions.Count == 0 || Extensions.Contains(Fold(extension.TrimStart('.'))));
    }

    /// <summary>Only a candidate filter. SQL still applies all literal predicates before LIMIT.</summary>
    public string? GetTrigramExpression(bool usePinyin)
    {
        static bool HasTrigram(string text) => text.EnumerateRunes().Take(3).Count() == 3;
        static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        var clauses = new List<string>();
        foreach (var term in NameTerms.Where(HasTrigram))
        {
            var literal = Quote(term);
            clauses.Add(usePinyin
                ? $"(name_key : {literal} OR initials_key : {literal})"
                : $"name_key : {literal}");
        }
        clauses.AddRange(PathTerms.Where(HasTrigram).Select(term => $"parent_key : {Quote(term)}"));
        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }
}

public static class SearchPath
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static string Prefix(string root) => root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

    // macOS can mount case-sensitive APFS volumes. Case-folding is ONLY for matching names.
    public static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(Prefix(root), StringComparison.Ordinal);

    // A slash-terminated prefix has a simple exclusive upper bound for SQLite BINARY ranges.
    public static string UpperBound(string root)
    {
        var prefix = Prefix(root);
        return prefix[..^1] + (char)(prefix[^1] + 1);
    }
}

public sealed record SearchOptions(bool HideSystemFiles = true, bool HideDotFiles = true,
    bool HideDotFolders = true, bool UsePinyin = true)
{
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini", ".Spotlight-V100", ".Trashes", ".fseventsd", ".localized"
    };

    public bool IsVisible(string path, string name, bool isDirectory, string root)
    {
        if (name.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase)) return false;
        if (HideSystemFiles && SystemNames.Contains(name)) return false;
        if (name.StartsWith('.') && (isDirectory ? HideDotFolders : HideDotFiles)) return false;
        if (!HideDotFolders) return true;
        var prefix = SearchPath.Prefix(root);
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        var segments = path[prefix.Length..].Split(Path.DirectorySeparatorChar);
        var directoryCount = isDirectory ? segments.Length : segments.Length - 1;
        for (var i = 0; i < directoryCount; i++)
            if (segments[i].StartsWith('.')) return false;
        return true;
    }
}
