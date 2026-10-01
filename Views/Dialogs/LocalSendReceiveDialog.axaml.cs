using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Services;

namespace MacExplorer.Views.Dialogs;

public partial class LocalSendReceiveDialog : DialogWindow
{
    private string _directory = "";
    private readonly TaskCompletionSource<LocalSendReceiveDecision> _unownedResult =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LocalSendReceiveDialog(LocalSendIncomingRequest request)
    {
        InitializeComponent();
        DeviceName.Text = request.Alias;
        ToolTip.SetTip(DeviceName, request.Alias);
        var total = request.Files.Sum(file => file.Size);
        FileSummary.Text = $"{request.Files.Count} 个文件 · {FormatSize(total)}";
        FileNames.ItemsSource = request.Files.Select(file => file.FileName + " · " + FormatSize(file.Size)).ToArray();
        _directory = request.DefaultDirectory;
        UpdateDirectory();
        Height = Math.Clamp(228 + request.Files.Count * 24, 290, 530);
        Closed += (_, _) => _unownedResult.TrySetResult(new LocalSendReceiveDecision(false, _directory));
    }

    public async Task<LocalSendReceiveDecision> ShowRequestAsync(Window? owner, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) Close(new LocalSendReceiveDecision(false, _directory));
        }));
        if (owner is { IsVisible: true })
            return await ShowDialog<LocalSendReceiveDecision>(owner)
                ?? new LocalSendReceiveDecision(false, _directory);
        Show();
        Activate();
        return await _unownedResult.Task;
    }

    private async void OnChooseDirectory(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenAuthorizedFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择接收位置", AllowMultiple = false
        });
        if (folders.Count == 0) return;
        var path = folders[0].Path.LocalPath;
        if (RuntimePaths.TestRoot is { } root && !Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return;
        _directory = path;
        UpdateDirectory();
    }

    private void UpdateDirectory()
    {
        DirectoryName.Text = _directory;
        ToolTip.SetTip(DirectoryName, _directory);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Complete(false);
    private void OnAccept(object? sender, RoutedEventArgs e) => Complete(true);

    private void Complete(bool accepted)
    {
        var result = new LocalSendReceiveDecision(accepted, _directory);
        _unownedResult.TrySetResult(result);
        Close(result);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        var unit = new[] { "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var index = -1;
        do { value /= 1024; index++; } while (value >= 1024 && index < unit.Length - 1);
        return $"{value:0.#} {unit[index]}";
    }
}
