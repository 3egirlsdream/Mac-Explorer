using CommunityToolkit.Mvvm.ComponentModel;
using MacExplorer.Models;

namespace MacExplorer.ViewModels;

public partial class BatchRenamePreviewRow(BatchRenamePreviewItem item) : ObservableObject
{
    public BatchRenamePreviewItem Item { get; } = item;
    public string OriginalName => Item.OriginalName;
    public string NewName => Item.NewName;
    public string DisplayedName => PreviewStep >= 0 && PreviewStep < Item.StepNames.Count ? Item.StepNames[PreviewStep] : NewName;
    public string DirectoryPath => Item.DirectoryPath;
    public FileSystemEntry? Entry => Item.Entry;
    public bool HasIssue => Item.HasError || Item.HasConflict || ExecutionError.Length > 0;
    public bool IsManual => Item.IsManual;
    public bool CanRestoreRuleResult => IsManual && IsEditable;
    public bool ShowDirectory { get; init; }
    private IReadOnlyList<BatchRenameRule> _rules = [];
    private int _selectedRule = -1;
    public IReadOnlyList<BatchRenameStepRow> Steps { get; private set; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayedName))] private int previewStep = -1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIssue))] private string executionError = "";
    [ObservableProperty] private bool isIncluded = item.IsIncluded;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRestoreRuleResult))] private bool isEditable = true;
    [ObservableProperty] private string editedName = item.NewName;
    [ObservableProperty] private string statusText = item.Status;
    partial void OnIsExpandedChanged(bool value) { if (value) BuildSteps(); }
    public void UpdateSteps(IReadOnlyList<BatchRenameRule> rules, int selectedRule, int previewStep)
    {
        _rules = rules; _selectedRule = selectedRule; PreviewStep = previewStep;
        if (IsExpanded) BuildSteps();
    }
    private void BuildSteps()
    {
        var steps = new List<BatchRenameStepRow>();
        for (var index = 0; index < Math.Min(_rules.Count, Item.StepNames.Count); index++)
        {
            var before = index == 0 ? OriginalName : Item.StepNames[index - 1];
            var after = Item.StepNames[index];
            steps.Add(new(index + 1, _rules[index].Label, before, after,
                !_rules[index].IsEnabled ? "已停用" : before == after ? "无变化" : "", index == _selectedRule));
        }
        if (IsManual) steps.Add(new(steps.Count + 1, "手动修改", Item.StepNames.LastOrDefault() ?? OriginalName, NewName, "", false));
        Steps = steps; OnPropertyChanged(nameof(Steps));
    }
}

public sealed record BatchRenameStepRow(int Number, string Label, string Before, string After, string State, bool IsSelected);
