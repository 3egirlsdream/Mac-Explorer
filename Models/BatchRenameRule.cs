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

public partial class BatchRenameRule : ObservableObject
{
    [ObservableProperty] private BatchRenameRuleType type;
    [ObservableProperty] private string findText = "";
    [ObservableProperty] private string replaceText = "";
    [ObservableProperty] private string prefixText = "";
    [ObservableProperty] private string suffixText = "";
    [ObservableProperty] private int sequenceStart = 1;
    [ObservableProperty] private int sequenceStep = 1;
    [ObservableProperty] private int sequencePadding = 3;
    [ObservableProperty] private string separator = "_";
    [ObservableProperty] private string dateFormat = "yyyy-MM-dd";
    [ObservableProperty] private CaseConversionMode caseMode;
    [ObservableProperty] private bool applyToExtension;
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private RenameTarget target;
    [ObservableProperty] private bool useRegex;
    [ObservableProperty] private bool caseSensitive;
    [ObservableProperty] private int matchOccurrence;
    [ObservableProperty] private RenamePosition placement = RenamePosition.After;
    [ObservableProperty] private int position;
    [ObservableProperty] private int removeLength = 1;
    [ObservableProperty] private string text = "";
    [ObservableProperty] private string templateText = "{name}_{n:000}";
    [ObservableProperty] private RenameDateSource dateSource;
    [ObservableProperty] private bool fallbackToModified;
    [ObservableProperty] private bool trimWhitespace = true;
    [ObservableProperty] private bool collapseWhitespace = true;
    [ObservableProperty] private bool normalizeSeparators;
    [ObservableProperty] private string extensionText = "";
    [ObservableProperty] private RenameAppliesTo appliesTo;
    [ObservableProperty] private string fileExtensions = "";

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
    partial void OnTypeChanged(BatchRenameRuleType value) => OnPropertyChanged(nameof(Label));
    public BatchRenameRule Clone() => System.Text.Json.JsonSerializer.Deserialize<BatchRenameRule>(
        System.Text.Json.JsonSerializer.Serialize(this))!;
}
