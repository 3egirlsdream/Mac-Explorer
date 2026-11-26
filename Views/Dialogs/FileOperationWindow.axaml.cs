using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Icons = MacExplorer.Assets.Icons;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

internal partial class FileOperationWindow : DialogWindow
{
    // Scale the 977 px reference window by the shared small button (24 DIP / 38 px).
    private const double ReferenceScale = 24d / 38;
    private static readonly double DefaultWidth = Math.Round(977 * ReferenceScale);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly BackgroundTaskInfo _task;
    private readonly FileOperationDisplay _display;
    private readonly IBackgroundTaskManager _manager;
    private double _fromProgress, _targetProgress;
    private long _progressStart;
    private bool _framePending;
    private bool _dismissed;
    private long _sampleTime;
    private double _sampleAmount;
    private double _rate;

    internal FileOperationWindow(IBackgroundTaskManager manager, BackgroundTaskInfo task)
    {
        InitializeComponent();
        _manager = manager; _task = task; _display = task.Request!.Operation!;
        var deletion = task.Kind == BackgroundTaskKind.Delete;
        Width = DefaultWidth;
        MinHeight = Math.Round((deletion ? 269 : _display.Kind == FileOperationPresentation.Paste ? 275 : 279) * ReferenceScale);
        Heading.Text = L("operation." + _display.Kind);
        Title = Heading.Text;
        Avalonia.Automation.AutomationProperties.SetName(this, Title);
        OperationIcon.Data = Geometry.Parse(deletion ? Icons.Delete : _display.Kind == FileOperationPresentation.Copy ? Icons.DocumentCopy : Icons.Folder);
        if (deletion)
        {
            IconTile.Bind(Border.BackgroundProperty, IconTile.GetResourceObservable("SurfaceMutedBrush"));
            OperationIcon.Bind(PathIcon.ForegroundProperty, OperationIcon.GetResourceObservable("TextMutedBrush"));
        }
        SizeChanged += (_, e) => Speed.IsVisible = e.NewSize.Width >= 500;
        var sources = task.Request.Sources;
        var first = sources.FirstOrDefault() ?? "";
        var name = sources.Count == 1 ? Path.GetFileName(first) : L("operation.count", sources.Count);
        var destination = task.Request.Destination;
        ToolTip.SetTip(Heading, Heading.Text);
        Route.Text = _display.Kind switch
        {
            FileOperationPresentation.Paste => L("operation.to", Path.GetFileName(destination)),
            FileOperationPresentation.EmptyTrash => L("operation.trash-location"),
            FileOperationPresentation.ArchiveUpdate => Path.GetFileName(destination),
            _ when deletion => L("operation.cleaning", name),
            _ => name + " → " + Path.GetFileName(destination)
        };
        ToolTip.SetTip(Route, Route.Text);
        Refresh();
    }

    internal static void Observe(IBackgroundTaskManager manager, BackgroundTaskInfo task, IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.Post(() =>
        {
            FileOperationWindow? window = null;
            bool Tick()
            {
                if (task.State is BackgroundTaskState.Completed or BackgroundTaskState.Cancelled || App.IsShuttingDown)
                {
                    window?.DismissAndClose();
                    return false;
                }
                if (window?._dismissed == true) return false;
                if (task.Request!.Operation!.Confirming) { window?.Hide(); return true; }
                if (!task.Request.Operation.ShouldShow(task.State)) return true;
                window ??= new(manager, task);
                if (!window.IsVisible)
                {
                    var owner = desktop.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
                    if (owner != null && owner.IsVisible)
                    {
                        if (owner.Screens.ScreenFromWindow(owner) is { } screen)
                            window.Width = Math.Min(DefaultWidth, screen.WorkingArea.Width / screen.Scaling - 24);
                        window.Show(owner);
                    }
                    else
                    {
                        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                        window.Show();
                    }
                }
                window.Refresh();
                return true;
            }
            if (Tick()) DispatcherTimer.Run(Tick, ProgressInterval);
        });
    }

    private static string L(string key, params object?[] args) => LocalizationText.Get(key, args);
    internal void DismissAndClose() { _dismissed = true; Close(); }

    internal void Refresh()
    {
        var state = _task.State;
        var failure = state is BackgroundTaskState.NeedsAttention or BackgroundTaskState.Failed;
        var uncertain = _display.Preparing || _display.FinderRunning || state == BackgroundTaskState.Waiting;
        var done = _display.ByItems ? _display.DoneItems : _display.DoneBytes;
        var total = _display.ByItems ? _display.TotalItems : _display.TotalBytes;
        Bar.IsIndeterminate = uncertain && state is BackgroundTaskState.Running or BackgroundTaskState.Waiting;
        var archiveUpdate = _display.Kind == FileOperationPresentation.ArchiveUpdate;
        var progress = archiveUpdate ? _task.Progress : total > 0 ? Math.Clamp(done * 100d / total, 0, 100) : 0;
        UpdateProgress(progress, !uncertain && state == BackgroundTaskState.Running);
        Percent.Text = uncertain ? "" : $"{Math.Round(progress)}%";
        Current.Text = failure ? _task.CurrentFile : state switch
        {
            BackgroundTaskState.Paused or BackgroundTaskState.Pausing or BackgroundTaskState.Cancelling or BackgroundTaskState.Waiting =>
                FileTaskPanelPresenter.StateText(_task),
            _ when _display.Preparing => L("operation.calculating"),
            _ => string.IsNullOrEmpty(_task.CurrentFile) ? "" : L("operation.current", Path.GetFileName(_task.CurrentFile))
        };
        ToolTip.SetTip(Current, _task.CurrentFile);
        Amount.Text = archiveUpdate || _display.Preparing || _display.FinderRunning ? "" : _display.ByItems
            ? L("operation.processed", done, total)
            : FileTaskPanelPresenter.FormatBytes(done) + " / " + FileTaskPanelPresenter.FormatBytes(total);
        UpdateRate(done, total, !archiveUpdate && !uncertain && state == BackgroundTaskState.Running);
        Pause.IsVisible = _task.Kind != BackgroundTaskKind.Delete && !failure;
        Pause.IsEnabled = state is BackgroundTaskState.Running or BackgroundTaskState.Waiting or BackgroundTaskState.Paused or BackgroundTaskState.Pausing;
        Pause.Content = L(state is BackgroundTaskState.Paused or BackgroundTaskState.Pausing ? "operation.resume" : "operation.pause");
        Stop.IsVisible = !failure && _task.CanCancel;
        Stop.IsEnabled = state != BackgroundTaskState.Cancelling;
        Retry.IsVisible = failure && _task.CanRetry;
        FailureSummary.IsVisible = failure;
        if (failure)
        {
            Results.ItemsSource = _task.Items;
            Error.Text = _task.ErrorMessage;
        }
    }

    private void UpdateProgress(double progress, bool running)
    {
        if (!running || !IsVisible || WindowState == WindowState.Minimized || progress < _targetProgress)
        {
            _fromProgress = _targetProgress = Bar.Value = progress;
            return;
        }
        if (progress == _targetProgress) return;
        _fromProgress = Bar.Value;
        _targetProgress = progress;
        _progressStart = Stopwatch.GetTimestamp();
        if (!_framePending)
        {
            _framePending = true;
            RequestAnimationFrame(AdvanceProgress);
        }
    }

    private void AdvanceProgress(TimeSpan timestamp)
    {
        _framePending = false;
        if (!IsVisible || WindowState == WindowState.Minimized || _task.State != BackgroundTaskState.Running
            || _fromProgress == _targetProgress) return;
        // Follow the display link (12 frames per sample at 120 Hz); never pass confirmed work.
        var fraction = Math.Min(1, Stopwatch.GetElapsedTime(_progressStart) / ProgressInterval);
        Bar.Value = _fromProgress + (_targetProgress - _fromProgress) * fraction;
        if (fraction < 1)
        {
            _framePending = true;
            RequestAnimationFrame(AdvanceProgress);
        }
    }

    private void UpdateRate(long done, long total, bool running)
    {
        if (!running) { _sampleTime = 0; _rate = 0; }
        else if (_sampleTime == 0) { _sampleTime = Stopwatch.GetTimestamp(); _sampleAmount = done; }
        else if (Stopwatch.GetElapsedTime(_sampleTime).TotalSeconds is >= .5 and var seconds)
        {
            _rate = Math.Max(0, done - _sampleAmount) / seconds;
            _sampleTime = Stopwatch.GetTimestamp(); _sampleAmount = done;
        }
        Speed.Text = _rate > 0 && !_display.ByItems ? FileTaskPanelPresenter.FormatBytes(_rate) + "/s" : "";
        Eta.Text = _rate > 0 && total > done ? L("operation.eta", Math.Ceiling((total - done) / _rate)) : "";
    }

    private void PauseOrResume(object? sender, RoutedEventArgs e)
    {
        if (_task.State is BackgroundTaskState.Paused or BackgroundTaskState.Pausing) _manager.ResumeTask(_task.Id);
        else _manager.PauseTask(_task.Id);
        Refresh();
    }
    private void StopOperation(object? sender, RoutedEventArgs e)
    {
        _manager.CancelTask(_task.Id);
        Refresh();
    }
    private void RetryOperation(object? sender, RoutedEventArgs e) => _manager.RetryTask(_task.Id);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_dismissed && !App.IsShuttingDown && _task.IsActive && _task.State != BackgroundTaskState.NeedsAttention)
        {
            e.Cancel = true;
            if (_task.CanCancel) _manager.CancelTask(_task.Id);
            else WindowState = WindowState.Minimized;
        }
        else
        {
            _dismissed = true;
            if (_task.State is BackgroundTaskState.Failed or BackgroundTaskState.NeedsAttention)
                _manager.RemoveTask(_task.Id);
        }
        base.OnClosing(e);
    }
}
