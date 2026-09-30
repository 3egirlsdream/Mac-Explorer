using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

internal static class BatchRenameRuleEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex Tokens = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Separators = new(@"[\s_-]+", RegexOptions.CultureInvariant, RegexTimeout);

    internal static List<BatchRenamePreviewItem> Generate(BatchRenameRequest request,
        IReadOnlyDictionary<string, DateTime?> photoDates, CancellationToken token)
    {
        var entries = request.Entries;
        if (request.Options.Sort is not (RenameSort.Input or RenameSort.Manual))
        {
            var ordered = request.Options.Sort switch
            {
                RenameSort.Name => entries.OrderBy(e => e.Name, NaturalNameComparer.Instance),
                RenameSort.Created => entries.OrderBy(e => e.Created),
                RenameSort.Modified => entries.OrderBy(e => e.LastModified),
                _ => entries.OrderBy(e => photoDates.GetValueOrDefault(e.FullPath) ?? (request.Options.PhotoSortFallbackToModified ? e.LastModified : DateTime.MaxValue))
            };
            entries = (request.Options.Descending ? ordered.Reverse() : ordered).ToArray();
        }
        var rules = request.Rules;
        var regexes = new Dictionary<int, Regex>();
        var ruleErrors = new Dictionary<int, string>();
        for (var r = 0; r < rules.Count; r++)
        {
            var rule = rules[r];
            try
            {
                ValidateRule(rule);
                if (rule.Type == BatchRenameRuleType.FindReplace && rule.FindText.Length > 0)
                    regexes[r] = new Regex(rule.UseRegex ? rule.FindText : Regex.Escape(rule.FindText),
                        RegexOptions.CultureInvariant | (rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), RegexTimeout);
            }
            catch (ArgumentException ex) { ruleErrors[r] = ex.Message; }
        }
        var counters = new Dictionary<(int, string), long>();
        var items = new List<BatchRenamePreviewItem>(entries.Count);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var item = new BatchRenamePreviewItem
            {
                Entry = entry, OriginalPath = entry.FullPath, OriginalName = entry.Name, NewName = entry.Name,
                IsIncluded = !request.ExcludedPaths.Contains(entry.FullPath),
                SourceSize = entry.Size, SourceModified = entry.LastModified, SourceCreated = entry.Created,
                SourceIsDirectory = entry.IsDirectory, SourceIsSymbolicLink = entry.IsSymbolicLink
            };
            var current = entry.Name;
            for (var r = 0; r < rules.Count; r++)
            {
                var rule = rules[r];
                if (item.IsIncluded && rule.IsEnabled && Applies(rule, entry))
                {
                    var key = (r, request.Options.RestartPerDirectory ? item.DirectoryPath : "");
                    var rank = counters.GetValueOrDefault(key);
                    counters[key] = rank + 1;
                    try
                    {
                        if (ruleErrors.TryGetValue(r, out var error)) throw new ArgumentException(error);
                        current = Apply(rule, current, entry, rank, request.CapturedAt,
                            photoDates.GetValueOrDefault(entry.FullPath), regexes.GetValueOrDefault(r));
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        ruleErrors[r] = "正则表达式超时，请简化匹配";
                        item.HasError = true; item.ErrorReason = $"第 {r + 1} 步：{ruleErrors[r]}";
                    }
                    catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                    {
                        item.HasError = true; item.ErrorReason = $"第 {r + 1} 步：{ex.Message}";
                    }
                }
                item.StepNames.Add(current);
            }
            if (item.IsIncluded && request.ManualNames.TryGetValue(entry.FullPath, out var manual))
            {
                current = manual; item.IsManual = true; item.HasError = false; item.ErrorReason = "";
            }
            item.NewName = current;
            var invalid = ValidateName(current);
            if (item.IsIncluded && invalid != null) { item.HasError = true; item.ErrorReason = invalid; }
            item.NewPath = Path.Combine(item.DirectoryPath, invalid == null ? current : entry.Name);
            if (item.IsIncluded && (entry.IsVirtual || !Path.IsPathFullyQualified(entry.FullPath)))
            { item.HasError = true; item.ErrorReason = "仅支持本地文件和文件夹"; }
            items.Add(item);
        }
        var parents = items.Where(i => i.IsIncluded && i.SourceIsDirectory && i.IsChanged)
            .GroupBy(i => i.OriginalPath, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var item in items.Where(i => i.IsIncluded))
        {
            var parent = item.DirectoryPath;
            while (parent.Length > 0)
            {
                if (parents.TryGetValue(parent, out var ancestor))
                {
                    item.HasError = ancestor.HasError = true;
                    item.ErrorReason = ancestor.ErrorReason = "父文件夹也参与重命名，请排除其中一方";
                    break;
                }
                parent = Path.GetDirectoryName(parent) ?? "";
            }
        }
        return items;
    }

    internal static void ValidateRule(BatchRenameRule rule)
    {
        if (new[] { rule.FindText, rule.ReplaceText, rule.PrefixText, rule.SuffixText, rule.Text, rule.TemplateText,
            rule.DateFormat, rule.Separator, rule.ExtensionText, rule.FileExtensions }.Any(value => value == null))
            throw new ArgumentException("规则文本不能为 null，请使用空字符串");
        if (!Enum.IsDefined(rule.Type) || !Enum.IsDefined(rule.Target) || !Enum.IsDefined(rule.CaseMode)
            || !Enum.IsDefined(rule.Placement) || !Enum.IsDefined(rule.DateSource) || !Enum.IsDefined(rule.AppliesTo))
            throw new ArgumentException("规则选项无效");
        if (rule.SequenceStart < 0 || rule.SequenceStep is < 1 or > 10000 || rule.SequencePadding is < 0 or > 12)
            throw new ArgumentException("编号范围无效（步长 1–10000，位数 0–12）");
        if (rule.Position < 0 || rule.RemoveLength < 0 || rule.MatchOccurrence < -1)
            throw new ArgumentException("字符位置或匹配序号无效");
        if (rule.UseRegex && string.IsNullOrEmpty(rule.FindText)) throw new ArgumentException("正则表达式不能为空");
    }

    internal static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return "文件名不能为空或为 . / ..";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(':') || name.Contains('/'))
            return "包含非法文件名字符";
        if (Encoding.UTF8.GetByteCount(name) > 255) return "文件名过长";
        return null;
    }

    internal static (string Name, string Extension) Split(string name, bool isDirectory)
    {
        if (isDirectory) return (name, "");
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? (name, "") : (name[..dot], name[dot..]);
    }

    private static bool Applies(BatchRenameRule rule, FileSystemEntry entry)
    {
        if (rule.AppliesTo == RenameAppliesTo.Files && entry.IsDirectory
            || rule.AppliesTo == RenameAppliesTo.Folders && !entry.IsDirectory) return false;
        return string.IsNullOrWhiteSpace(rule.FileExtensions) || !entry.IsDirectory &&
            rule.FileExtensions.Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(ext => string.Equals(ext.TrimStart('*', '.'), Split(entry.Name, false).Extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }

    private static string Apply(BatchRenameRule rule, string current, FileSystemEntry entry, long rank,
        DateTime capturedAt, DateTime? photoDate, Regex? regex)
    {
        var (stem, extension) = Split(current, entry.IsDirectory);
        var target = rule.ApplyToExtension && rule.Type is BatchRenameRuleType.FindReplace or BatchRenameRuleType.CaseConversion
            ? RenameTarget.FullName : rule.Target;
        if (rule.Type == BatchRenameRuleType.Extension)
        {
            if (entry.IsDirectory) return current;
            return stem + (rule.ExtensionText.TrimStart('.') is { Length: > 0 } ext ? "." + ext : "");
        }
        if (entry.IsDirectory && target == RenameTarget.Extension) return current;
        var input = entry.IsDirectory || target == RenameTarget.FullName ? current
            : target == RenameTarget.Extension ? extension.TrimStart('.') : stem;
        var number = checked(rule.SequenceStart + rank * rule.SequenceStep);
        var output = rule.Type switch
        {
            BatchRenameRuleType.FindReplace => Replace(input, rule, regex),
            BatchRenameRuleType.AddPrefix => rule.PrefixText + input,
            BatchRenameRuleType.AddSuffix => input + rule.SuffixText,
            BatchRenameRuleType.InsertText => Insert(input, rule.Text, rule),
            BatchRenameRuleType.RemoveText => Remove(input, rule.Position, rule.RemoveLength),
            BatchRenameRuleType.Sequence => Insert(input, number.ToString(CultureInfo.InvariantCulture).PadLeft(rule.SequencePadding, '0'), rule, true),
            BatchRenameRuleType.Date => Insert(input, Date(rule.DateSource, rule, entry, capturedAt, photoDate)
                .ToString(rule.DateFormat, CultureInfo.InvariantCulture), rule, true),
            BatchRenameRuleType.CaseConversion => rule.CaseMode switch
            {
                CaseConversionMode.Uppercase => input.ToUpperInvariant(),
                CaseConversionMode.Lowercase => input.ToLowerInvariant(),
                _ => Regex.Replace(input.ToLowerInvariant(), @"(^|[\s_-])(\p{L})",
                    match => match.Groups[1].Value + match.Groups[2].Value.ToUpperInvariant(), RegexOptions.CultureInvariant, RegexTimeout)
            },
            BatchRenameRuleType.Template => Template(rule, input, entry, number, capturedAt, photoDate),
            BatchRenameRuleType.Cleanup => Clean(input, rule),
            _ => input
        };
        return entry.IsDirectory || target == RenameTarget.FullName ? output
            : target == RenameTarget.Extension ? stem + (output.Length > 0 ? "." + output.TrimStart('.') : "") : output + extension;
    }

    private static string Replace(string input, BatchRenameRule rule, Regex? regex)
    {
        if (regex == null) return input;
        var matches = rule.MatchOccurrence == -1 ? regex.Matches(input).Count : 0;
        var index = 0;
        return regex.Replace(input, match =>
        {
            index++;
            return rule.MatchOccurrence == 0 || index == (rule.MatchOccurrence == -1 ? matches : rule.MatchOccurrence)
                ? rule.UseRegex ? match.Result(rule.ReplaceText) : rule.ReplaceText : match.Value;
        });
    }

    private static int CharacterOffset(string input, int position)
    {
        var offsets = StringInfo.ParseCombiningCharacters(input);
        return position >= offsets.Length ? input.Length : offsets[position];
    }
    private static string Insert(string input, string value, BatchRenameRule rule, bool separated = false) => rule.Placement switch
    {
        RenamePosition.Before => value + (separated && input.Length > 0 ? rule.Separator : "") + input,
        RenamePosition.After => input + (separated && input.Length > 0 ? rule.Separator : "") + value,
        _ => input.Insert(CharacterOffset(input, rule.Position), value)
    };
    private static string Remove(string input, int position, int length)
    {
        var start = CharacterOffset(input, position);
        var end = CharacterOffset(input, (int)Math.Min(int.MaxValue, (long)position + length));
        return input.Remove(start, end - start);
    }
    private static string Clean(string input, BatchRenameRule rule)
    {
        if (rule.TrimWhitespace) input = input.Trim();
        if (rule.CollapseWhitespace) input = Spaces.Replace(input, " ");
        if (rule.NormalizeSeparators) input = Separators.Replace(input, rule.Separator);
        return input;
    }
    private static DateTime Date(RenameDateSource source, BatchRenameRule rule, FileSystemEntry entry, DateTime now, DateTime? photo)
    {
        var date = source switch
        {
            RenameDateSource.Current => now, RenameDateSource.Created => entry.Created,
            RenameDateSource.Modified => entry.LastModified,
            _ => photo ?? (rule.FallbackToModified ? entry.LastModified : default)
        };
        return date == default ? throw new ArgumentException(source == RenameDateSource.PhotoTaken ? "缺少拍摄时间" : "缺少文件日期") : date;
    }
    private static string Template(BatchRenameRule rule, string name, FileSystemEntry entry, long number, DateTime now, DateTime? photo)
    {
        var literal = Tokens.Replace(rule.TemplateText, "");
        if (literal.Contains('{') || literal.Contains('}')) throw new ArgumentException("字段括号不完整");
        var output = Tokens.Replace(rule.TemplateText, match =>
        {
            var parts = match.Groups[1].Value.Split(':', 2);
            var format = parts.Length == 2 ? parts[1] : "";
            return parts[0] switch
            {
                "name" or "名称" => name,
                "parent" or "目录" => Path.GetFileName(Path.GetDirectoryName(entry.FullPath)) ?? "",
                "n" or "编号" => number.ToString(format.Length > 0 ? format : new string('0', rule.SequencePadding), CultureInfo.InvariantCulture),
                "date" or "日期" => Date(rule.DateSource, rule, entry, now, photo).ToString(format.Length > 0 ? format : rule.DateFormat, CultureInfo.InvariantCulture),
                "created" or "创建日期" => Date(RenameDateSource.Created, rule, entry, now, photo).ToString(format.Length > 0 ? format : rule.DateFormat, CultureInfo.InvariantCulture),
                "modified" or "修改日期" => Date(RenameDateSource.Modified, rule, entry, now, photo).ToString(format.Length > 0 ? format : rule.DateFormat, CultureInfo.InvariantCulture),
                "taken" or "拍摄日期" => Date(RenameDateSource.PhotoTaken, rule, entry, now, photo).ToString(format.Length > 0 ? format : rule.DateFormat, CultureInfo.InvariantCulture),
                _ => throw new ArgumentException($"未知字段：{parts[0]}")
            };
        });
        return output;
    }

    internal static bool NeedsPhotoDates(BatchRenameRequest request) => request.Options.Sort == RenameSort.PhotoTaken
        || request.Rules.Any(rule => rule.IsEnabled && (rule.DateSource == RenameDateSource.PhotoTaken
            || rule.Type == BatchRenameRuleType.Template && (rule.TemplateText.Contains("{taken", StringComparison.Ordinal)
                || rule.TemplateText.Contains("{拍摄日期", StringComparison.Ordinal))));

    private sealed class NaturalNameComparer : IComparer<string>
    {
        internal static readonly NaturalNameComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            var a = 0; var b = 0;
            while (a < x.Length && b < y.Length)
            {
                if (char.IsAsciiDigit(x[a]) && char.IsAsciiDigit(y[b]))
                {
                    var ax = a; var by = b;
                    while (a < x.Length && char.IsAsciiDigit(x[a])) a++;
                    while (b < y.Length && char.IsAsciiDigit(y[b])) b++;
                    var left = x.AsSpan(ax, a - ax).TrimStart('0'); var right = y.AsSpan(by, b - by).TrimStart('0');
                    var comparison = left.Length.CompareTo(right.Length);
                    if (comparison == 0) comparison = left.SequenceCompareTo(right);
                    if (comparison != 0) return comparison;
                }
                else
                {
                    var comparison = char.ToUpperInvariant(x[a++]).CompareTo(char.ToUpperInvariant(y[b++]));
                    if (comparison != 0) return comparison;
                }
            }
            return (x.Length - a).CompareTo(y.Length - b);
        }
    }
}
