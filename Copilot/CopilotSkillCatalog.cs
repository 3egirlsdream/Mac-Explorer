namespace MacExplorer.Copilot;

public sealed record CopilotSkill(string Name, string Content, bool Enabled, bool BuiltIn);

public sealed class CopilotSkillCatalog
{
    private readonly string _builtIn = Path.Combine(AppContext.BaseDirectory, "Copilot", "Skills");
    public string DirectoryPath { get; } = Path.Combine(RuntimePaths.LocalApplicationData, "MacExplorer", "CopilotSkills");

    public CopilotSkillCatalog()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!Directory.Exists(_builtIn)) return;
        foreach (var file in Directory.EnumerateFiles(_builtIn, "SKILL.md", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(Path.GetDirectoryName(file)!);
            var target = Path.Combine(DirectoryPath, name, "SKILL.md");
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public IReadOnlyList<CopilotSkill> List() => Directory.EnumerateDirectories(DirectoryPath)
        .Select(path => new { Path = path, SkillFile = Path.Combine(path, "SKILL.md") })
        .Where(item => File.Exists(item.SkillFile))
        .Select(item => new CopilotSkill(Path.GetFileName(item.Path), File.ReadAllText(item.SkillFile),
            !File.Exists(Path.Combine(item.Path, ".disabled")),
            File.Exists(Path.Combine(_builtIn, Path.GetFileName(item.Path), "SKILL.md"))))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Save(string name, string content)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(content) || !content.StartsWith("---\n", StringComparison.Ordinal))
            throw new ArgumentException("技能须包含 SKILL.md 的 YAML 头部。", nameof(content));
        var end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) throw new ArgumentException("技能 YAML 头部缺少结束标记。", nameof(content));
        var header = content[4..end];
        var declaredName = header.Split('\n').FirstOrDefault(line => line.StartsWith("name:", StringComparison.Ordinal))?
            .Split(':', 2)[1].Trim().Trim('"', '\'');
        if (declaredName != name) throw new ArgumentException("技能目录名与 YAML 中的 name 必须一致。", nameof(content));
        if (!header.Split('\n').Any(line => line.StartsWith("description:", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(line["description:".Length..])))
            throw new ArgumentException("技能须填写 description。", nameof(content));
        var directory = Path.Combine(DirectoryPath, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
    }

    public void SetEnabled(string name, bool enabled)
    {
        ValidateName(name);
        var directory = Path.Combine(DirectoryPath, name);
        if (!File.Exists(Path.Combine(directory, "SKILL.md"))) throw new FileNotFoundException("技能不存在。");
        var marker = Path.Combine(directory, ".disabled");
        if (enabled) File.Delete(marker);
        else File.WriteAllText(marker, string.Empty);
    }

    public void Reset(string name)
    {
        ValidateName(name);
        var original = Path.Combine(_builtIn, name, "SKILL.md");
        if (!File.Exists(original)) throw new InvalidOperationException("该技能没有内置版本。");
        var target = Path.Combine(DirectoryPath, name, "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(original, target, overwrite: true);
        File.Delete(Path.Combine(DirectoryPath, name, ".disabled"));
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsDigit(c) || c == '-')))
            throw new ArgumentException("技能名称只允许小写字母、数字和连字符。");
    }
}
