using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Indexing;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

internal sealed class DatabaseOptimizationDialog : DialogWindow
{
    private readonly string _databasePath;
    private readonly SearchCatalog? _searchCatalog;
    private readonly ApplicationStorageMaintenance? _storageMaintenance;
    private readonly TextBlock _sizeValue;
    private readonly TextBlock _reclaimableValue;
    private readonly TextBlock _modeValue;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly StackPanel _actionsPanel;
    private readonly AppButton _cancelButton;
    private readonly AppButton _startButton;
    private CancellationTokenSource? _cts;
    private bool _running;

    internal bool Completed { get; private set; }

    internal DatabaseOptimizationDialog(string? databasePath = null, DatabaseStorageStatus? initialStatus = null)
    {
        _databasePath = databasePath ?? RuntimePaths.DatabasePath;
        _searchCatalog = App.Services?.GetService<SearchCatalog>();
        _storageMaintenance = App.Services?.GetService<ApplicationStorageMaintenance>();

        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new DialogSettingsStyles());

        var title = LocalizationText.Get("storage.optimize.title");
        Title = title;
        TitleBarContent = new TextBlock { Text = title, Classes = { "dialog-window-title", "centered" } };
        ToolTip.SetTip(TitleBarContent, title);
        Width = 440;
        MinWidth = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _sizeValue = new TextBlock { Classes = { "settings-description" }, VerticalAlignment = VerticalAlignment.Center };
        _reclaimableValue = new TextBlock { Classes = { "settings-description" }, VerticalAlignment = VerticalAlignment.Center };
        _modeValue = new TextBlock { Classes = { "settings-description" }, VerticalAlignment = VerticalAlignment.Center };

        var infoGroup = new Border
        {
            Classes = { "settings-group" },
            Child = new StackPanel
            {
                Children =
                {
                    CreateInfoRow(LocalizationText.Get("storage.optimize.current_size"), _sizeValue, divider: true),
                    CreateInfoRow(LocalizationText.Get("storage.optimize.reclaimable"), _reclaimableValue, divider: true),
                    CreateInfoRow(LocalizationText.Get("storage.optimize.mode"), _modeValue, divider: false)
                }
            }
        };

        _statusText = new TextBlock
        {
            Text = LocalizationText.Get("storage.optimize.description"),
            TextWrapping = TextWrapping.Wrap,
            Classes = { "settings-description" }
        };

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 4,
            IsVisible = false
        };

        _cancelButton = new AppButton
        {
            Content = LocalizationText.Get("取消"),
            Classes = { "secondary", "compact" }
        };
        _startButton = new AppButton
        {
            Content = LocalizationText.Get("storage.optimize.start"),
            Classes = { "primary", "compact" }
        };
        _cancelButton.Click += (_, _) => OnCancelClick();
        _startButton.Click += (_, _) => _ = RunOptimizationAsync();

        _actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { _cancelButton, _startButton }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children = { infoGroup, _statusText, _progressBar, _actionsPanel }
        };

        ApplyStatus(initialStatus);
        Opened += (_, _) =>
        {
            _startButton.Focus();
            if (initialStatus == null) _ = RefreshStatusAsync();
        };
        Closing += (_, _) => _cts?.Cancel();
    }

    private static Border CreateInfoRow(string label, TextBlock value, bool divider)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        var labelBlock = new TextBlock
        {
            Text = label,
            Classes = { "settings-label" },
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(labelBlock);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        var border = new Border { Child = grid };
        border.Classes.Add("settings-row");
        if (divider) border.Classes.Add("settings-divider");
        return border;
    }

    private async Task RefreshStatusAsync()
    {
        var status = await Task.Run(() => DatabaseSpaceMaintenance.Inspect(_databasePath));
        ApplyStatus(status);
    }

    private void ApplyStatus(DatabaseStorageStatus? status)
    {
        if (status is not { } s)
        {
            _sizeValue.Text = "—";
            _reclaimableValue.Text = "—";
            _modeValue.Text = "—";
            return;
        }
        _sizeValue.Text = FileListStatusFormatter.FormatBytes(s.TotalBytes);
        _reclaimableValue.Text = s.FreeBytes > 0
            ? FileListStatusFormatter.FormatBytes(s.FreeBytes)
            : LocalizationText.Get("storage.optimize.none");
        _modeValue.Text = s.IsIncremental
            ? LocalizationText.Get("storage.mode.incremental")
            : LocalizationText.Get("storage.mode.legacy");
    }

    private void OnCancelClick()
    {
        if (_running)
        {
            _cancelButton.IsEnabled = false;
            _statusText.Text = LocalizationText.Get("storage.optimize.cancelling");
            _cts?.Cancel();
        }
        else
        {
            Close(Completed);
        }
    }

    private async Task RunOptimizationAsync()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _startButton.IsVisible = false;
        _cancelButton.IsEnabled = true;
        _progressBar.IsVisible = true;
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        _statusText.Text = LocalizationText.Get("storage.optimize.stage.preparing");

        IDisposable? searchLease = null;
        IDisposable? maintenanceLease = null;
        try
        {
            if (_storageMaintenance != null)
                maintenanceLease = await _storageMaintenance.AcquireExclusiveMaintenanceAsync(token);
            if (_searchCatalog != null)
                searchLease = await _searchCatalog.AcquireExclusiveMaintenanceAsync(token);

            var progress = new Progress<DatabaseOptimizationProgress>(report =>
                Dispatcher.UIThread.Post(() => ApplyProgress(report)));

            var result = await DatabaseSpaceMaintenance.ConvertToIncrementalAsync(_databasePath, progress, token);
            Completed = true;
            _progressBar.IsVisible = false;
            _actionsPanel.IsVisible = false;
            _statusText.Text = result.FreedBytes > 0
                ? LocalizationText.Get("storage.optimize.done_freed", FileListStatusFormatter.FormatBytes(result.FreedBytes))
                : LocalizationText.Get("storage.optimize.done");
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            _progressBar.IsVisible = false;
            _startButton.Content = LocalizationText.Get("storage.optimize.retry");
            _startButton.IsVisible = true;
            _cancelButton.IsEnabled = true;
            _statusText.Text = LocalizationText.Get("storage.optimize.cancelled");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            _progressBar.IsVisible = false;
            _startButton.Content = LocalizationText.Get("storage.optimize.retry");
            _startButton.IsVisible = true;
            _cancelButton.IsEnabled = true;
            _statusText.Text = LocalizationText.Get("storage.optimize.failed", ex.Message);
            await RefreshStatusAsync();
        }
        finally
        {
            searchLease?.Dispose();
            maintenanceLease?.Dispose();
            _cts?.Dispose();
            _cts = null;
            _running = false;
        }
    }

    private void ApplyProgress(DatabaseOptimizationProgress report)
    {
        _progressBar.IsIndeterminate = report.IsIndeterminate;
        if (!report.IsIndeterminate) _progressBar.Value = report.Percentage;
        _statusText.Text = report.Stage switch
        {
            DatabaseOptimizationStage.Preparing => LocalizationText.Get("storage.optimize.stage.preparing"),
            DatabaseOptimizationStage.Compacting => LocalizationText.Get("storage.optimize.stage.compacting"),
            DatabaseOptimizationStage.Finalizing => LocalizationText.Get("storage.optimize.stage.finalizing"),
            _ => _statusText.Text
        };
    }
}
