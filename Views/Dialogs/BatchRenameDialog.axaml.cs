using Avalonia.Controls;
using Avalonia.Interactivity;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class BatchRenameDialog : DialogWindow
{
    private FileListViewModel? _viewModel;
    private List<BatchRenamePreviewItem> _previewItems = [];

    public BatchRenameDialog()
    {
        InitializeComponent();
    }

    public Task<bool> ShowDialogAsync(Window owner, FileListViewModel viewModel)
    {
        _viewModel = viewModel;
        RefreshPreview();
        return base.ShowDialog<bool>(owner);
    }

    private BatchRenameRule BuildRule()
    {
        var rule = new BatchRenameRule();

        switch (RuleTypeCombo.SelectedIndex)
        {
            case 0:
                rule.Type = BatchRenameRuleType.FindReplace;
                rule.FindText = FindTextBox.Text ?? "";
                rule.ReplaceText = ReplaceTextBox.Text ?? "";
                break;
            case 1:
                rule.Type = BatchRenameRuleType.AddPrefix;
                rule.PrefixText = PrefixTextBox.Text ?? "";
                break;
            case 2:
                rule.Type = BatchRenameRuleType.AddSuffix;
                rule.SuffixText = SuffixTextBox.Text ?? "";
                break;
            case 3:
                rule.Type = BatchRenameRuleType.Sequence;
                rule.SequenceStart = (int)(SeqStartBox.Value ?? 1);
                rule.SequenceStep = (int)(SeqStepBox.Value ?? 1);
                rule.SequencePadding = (int)(SeqPadBox.Value ?? 2);
                break;
            case 4:
                rule.Type = BatchRenameRuleType.Date;
                rule.DateFormat = DateFormatBox.Text ?? "yyyy-MM-dd";
                break;
            case 5:
                rule.Type = BatchRenameRuleType.CaseConversion;
                rule.CaseMode = CaseModeCombo.SelectedIndex switch
                {
                    0 => CaseConversionMode.Uppercase,
                    1 => CaseConversionMode.Lowercase,
                    _ => CaseConversionMode.TitleCase
                };
                break;
        }

        return rule;
    }

    private void RefreshPreview()
    {
        if (_viewModel == null) return;

        var selected = _viewModel.SelectedEntries.ToList();
        if (selected.Count == 0)
        {
            _previewItems = [];
            PreviewList.ItemsSource = _previewItems;
            StatusText.Content = "请选择要重命名的项目";
            ApplyButton.IsEnabled = false;
            return;
        }

        var rule = BuildRule();
        var service = App.Services.GetService<IBatchRenameService>();
        if (service == null) return;

        _previewItems = service.GeneratePreview(selected, [rule]);
        PreviewList.ItemsSource = _previewItems;

        var changed = _previewItems.Count(i => i.IsChanged && !i.HasError && !i.HasConflict);
        var errors = _previewItems.Count(i => i.HasError);
        var conflicts = _previewItems.Count(i => i.HasConflict);
        StatusText.Content = $"{changed} 项将重命名" +
            (errors > 0 ? $"，{errors} 项有错误" : "") +
            (conflicts > 0 ? $"，{conflicts} 项有冲突" : "");

        ApplyButton.IsEnabled = changed > 0 && errors == 0 && conflicts == 0;
    }

    private void OnRuleTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Guard against XAML initialization order: SelectedIndex="0" in XAML
        // fires SelectionChanged before sibling controls are created.
        if (FindReplaceSection == null) return;
        FindReplaceSection.IsVisible = RuleTypeCombo.SelectedIndex == 0;
        PrefixSection.IsVisible = RuleTypeCombo.SelectedIndex == 1;
        SuffixSection.IsVisible = RuleTypeCombo.SelectedIndex == 2;
        SequenceSection.IsVisible = RuleTypeCombo.SelectedIndex == 3;
        DateSection.IsVisible = RuleTypeCombo.SelectedIndex == 4;
        CaseSection.IsVisible = RuleTypeCombo.SelectedIndex == 5;
        RefreshPreview();
    }

    private void OnRuleChanged(object? sender, RoutedEventArgs e) => RefreshPreview();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var service = App.Services.GetService<BatchRenameOperationService>();
        if (service == null) { Close(false); return; }

        ApplyButton.IsEnabled = false;
        ApplyButton.Content = "正在重命名...";

        try
        {
            var result = await service.ExecuteAsync(_previewItems);

            if (result.FailedCount > 0)
            {
                StatusText.Content = $"成功 {result.SuccessCount}，失败 {result.FailedCount}";
                ApplyButton.IsEnabled = true;
                ApplyButton.Content = "重试";
                return;
            }

            await _viewModel.RefreshAsync();
            Close(true);
        }
        catch (OperationCanceledException)
        {
            StatusText.Content = "已取消";
            ApplyButton.IsEnabled = true;
            ApplyButton.Content = "应用";
        }
        catch
        {
            ApplyButton.IsEnabled = true;
            ApplyButton.Content = "应用";
        }
    }
}
