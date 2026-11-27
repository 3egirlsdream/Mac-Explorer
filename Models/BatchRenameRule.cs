using MacExplorer.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MacExplorer.Models;

// Existing values retain their wire representation for saved Copilot calls.
public enum BatchRenameRuleType
{
    FindReplace, AddPrefix, AddSuffix, Sequence, Date, CaseConversion,
    InsertText, RemoveText, Template, Cleanup, Extension
}
public enum CaseConversionMode { Uppercase, Lowercase, TitleCase }
public enum RenameTarget { Name, Extension, FullName }
public enum RenamePosition { Before, After, AtPosition }
public enum RenameDateSource { Current, Created, Modified, PhotoTaken }
public enum RenameSort { Input, Name, Created, Modified, PhotoTaken, Manual }
public enum RenameAppliesTo { All, Files, Folders }

public class BatchRenameRule : ObservableObject
{
    public BatchRenameRuleType Type
    {
        get => field;
        set
        {
            if (field == value) return;
            OnPropertyChanging();
            field = value;
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged();
        }
    }

    public string FindText { get; set => SetProperty(ref field, value); } = "";
    public string ReplaceText { get; set => SetProperty(ref field, value); } = "";
    public string PrefixText { get; set => SetProperty(ref field, value); } = "";
    public string SuffixText { get; set => SetProperty(ref field, value); } = "";
    public int SequenceStart { get; set => SetProperty(ref field, value); } = 1;
    public int SequenceStep { get; set => SetProperty(ref field, value); } = 1;
    public int SequencePadding { get; set => SetProperty(ref field, value); } = 3;
    public string Separator { get; set => SetProperty(ref field, value); } = "_";
    public string DateFormat { get; set => SetProperty(ref field, value); } = "yyyy-MM-dd";
    public CaseConversionMode CaseMode { get; set => SetProperty(ref field, value); }
    public bool ApplyToExtension { get; set => SetProperty(ref field, value); }
    public bool IsEnabled { get; set => SetProperty(ref field, value); } = true;
    public RenameTarget Target { get; set => SetProperty(ref field, value); }
    public bool UseRegex { get; set => SetProperty(ref field, value); }
    public bool CaseSensitive { get; set => SetProperty(ref field, value); }
    public int MatchOccurrence { get; set => SetProperty(ref field, value); }
    public RenamePosition Placement { get; set => SetProperty(ref field, value); } = RenamePosition.After;
    public int Position { get; set => SetProperty(ref field, value); }
    public int RemoveLength { get; set => SetProperty(ref field, value); } = 1;
    public string Text { get; set => SetProperty(ref field, value); } = "";
    public string TemplateText { get; set => SetProperty(ref field, value); } = "{name}_{n:000}";
    public RenameDateSource DateSource { get; set => SetProperty(ref field, value); }
    public bool FallbackToModified { get; set => SetProperty(ref field, value); }
    public bool TrimWhitespace { get; set => SetProperty(ref field, value); } = true;
    public bool CollapseWhitespace { get; set => SetProperty(ref field, value); } = true;
    public bool NormalizeSeparators { get; set => SetProperty(ref field, value); }
    public string ExtensionText { get; set => SetProperty(ref field, value); } = "";
    public RenameAppliesTo AppliesTo { get; set => SetProperty(ref field, value); }
    public string FileExtensions { get; set => SetProperty(ref field, value); } = "";

    [System.Text.Json.Serialization.JsonPropertyOrder(-1)]
    public string Label => Type switch
    {
        BatchRenameRuleType.FindReplace => "替换文本",
        BatchRenameRuleType.AddPrefix => "添加前缀",
        BatchRenameRuleType.AddSuffix => "添加后缀",
        BatchRenameRuleType.Sequence => "添加编号",
        BatchRenameRuleType.Date => "添加日期",
        BatchRenameRuleType.CaseConversion => "大小写",
        BatchRenameRuleType.InsertText => "插入文本",
        BatchRenameRuleType.RemoveText => "删除文本",
        BatchRenameRuleType.Template => "自定义名称",
        BatchRenameRuleType.Cleanup => "清理名称",
        _ => "修改扩展名"
    };
    public BatchRenameRule Clone() => System.Text.Json.JsonSerializer.Deserialize(
        System.Text.Json.JsonSerializer.Serialize(this, AppJsonContext.Default.BatchRenameRule), AppJsonContext.Default.BatchRenameRule)!;
}
