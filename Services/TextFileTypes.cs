namespace MacExplorer.Services;

internal static class TextFileTypes
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml",
        ".yaml", ".yml", ".ini", ".conf", ".config", ".toml", ".properties", ".env", ".editorconfig",
        ".gitignore", ".gitattributes", ".dockerignore", ".npmignore", ".nfo", ".readme", ".rst", ".tex",
        ".adoc", ".asciidoc", ".org", ".srt", ".vtt", ".ass", ".ssa", ".lrc", ".cue", ".sh", ".zsh",
        ".bash", ".fish", ".ps1", ".cs", ".fs", ".vb", ".js", ".jsx", ".ts", ".tsx",
        ".html", ".htm", ".css", ".scss", ".less", ".py", ".rb", ".php", ".java", ".kt",
        ".kts", ".swift", ".m", ".mm", ".h", ".hpp", ".c", ".cpp", ".go", ".rs", ".sql",
        ".graphql", ".gql", ".ipynb", ".lua", ".r", ".rmd", ".scala", ".sc", ".clj", ".cljs",
        ".groovy", ".gradle", ".dart", ".ex", ".exs", ".erl", ".hrl", ".pl", ".pm", ".t", ".vim",
        ".asm", ".s", ".f", ".f90", ".pas", ".d", ".zig", ".sol", ".vue", ".svelte", ".astro",
        ".make", ".mk", ".cmake", ".dockerfile"
    };
    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "README.md", "README.txt", "CHANGELOG", "CHANGES", "NEWS", "LICENSE", "COPYING",
        "NOTICE", "AUTHORS", "CONTRIBUTORS", "Makefile", "GNUmakefile", "Dockerfile", "Containerfile",
        "Gemfile", "Rakefile", "Podfile", "Brewfile", "Procfile", "Vagrantfile"
    };

    public static bool IsText(string path)
        => TextExtensions.Contains(Path.GetExtension(path)) || TextFileNames.Contains(Path.GetFileName(path));
}
