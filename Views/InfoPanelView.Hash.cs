using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class InfoPanelView
{
    private CancellationTokenSource? _hashCts;
    private string? _hashValue;
    private string? _hashPath;

    private static bool CanHash(FileSystemEntry entry)
        => !entry.IsDirectory && !entry.IsVirtual && Path.IsPathFullyQualified(entry.FullPath);

    private void PrepareHash(FileSystemEntry entry)
    {
        if (!CanHash(entry)) return;
        if (!IsLivePreviewEnabled || entry.Size > FileHashCalculator.AutomaticByteLimit)
            ShowHashAction("按需计算", "点击计算 SHA-256；大文件和未激活窗格不会自动读取全部内容。");
        else
            BeginHashLoad(entry.FullPath, automatic: true);
    }

    private void ResetHash() => CancelHashLoad();

    private void CancelHashLoad()
    {
        var request = _hashCts;
        _hashCts = null;
        request?.Cancel(); // The async request owns disposal after its I/O has stopped.
        _hashValue = null;
        _hashPath = null;
        InfoHash.Text = "—";
        ToolTip.SetTip(InfoHash, null);
        CopyHashBtn.IsVisible = false;
        ComputeHashBtn.IsVisible = false;
    }

    private void ShowHashAction(string text, string tip)
    {
        InfoHash.Text = text;
        ToolTip.SetTip(InfoHash, tip);
        CopyHashBtn.IsVisible = false;
        ComputeHashBtn.Content = "计算";
        ComputeHashBtn.IsVisible = true;
    }

    private void ComputeHash(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedEntries.Count != 1 || !CanHash(ViewModel.SelectedEntries[0])) return;
        if (_hashCts != null)
        {
            CancelHashLoad();
            ShowHashAction("已取消", "点击重新计算 SHA-256。");
            return;
        }
        BeginHashLoad(ViewModel.SelectedEntries[0].FullPath, automatic: false);
    }

    private void BeginHashLoad(string path, bool automatic)
    {
        CancelHashLoad();
        var request = new CancellationTokenSource();
        _hashCts = request;
        InfoHash.Text = "计算中…";
        ComputeHashBtn.Content = "取消";
        ComputeHashBtn.IsVisible = true;
        _ = LoadHashAsync(path, automatic, request);
    }

    private bool IsCurrentHashRequest(string path, CancellationTokenSource request)
        => ReferenceEquals(_hashCts, request) && !request.IsCancellationRequested
           && ViewModel?.IsInfoPanelVisible == true && ViewModel.SelectedEntries.Count == 1
           && ViewModel.SelectedEntries[0].FullPath == path;

    // Called on the UI thread: await resumes on the same Avalonia context. Only the
    // calculation runs in the pool; there is no second generation counter or queued UI result.
    private async Task LoadHashAsync(string path, bool automatic, CancellationTokenSource request)
    {
        try
        {
            var hash = await Task.Run(() => FileHashCalculator.ComputeAsync(path, request.Token,
                automatic ? FileHashCalculator.AutomaticByteLimit : long.MaxValue));
            if (!IsCurrentHashRequest(path, request)) return;
            if (hash == null)
            {
                ShowHashAction("按需计算", "文件超过自动计算范围，点击计算 SHA-256。");
                return;
            }
            _hashValue = hash;
            _hashPath = path;
            InfoHash.Text = hash;
            ToolTip.SetTip(InfoHash, hash);
            ComputeHashBtn.IsVisible = false;
            CopyHashBtn.IsVisible = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"SHA-256 {path}: {ex}");
            if (IsCurrentHashRequest(path, request))
                ShowHashAction("计算失败", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_hashCts, request)) _hashCts = null;
            request.Dispose();
        }
    }

    private async void CopyHash(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm?.SelectedEntries.Count != 1 || _hashValue == null || vm.SelectedEntries[0].FullPath != _hashPath) return;
        var clipboard = _clipboardService ?? App.Services?.GetService<IClipboardService>();
        if (clipboard == null) return;
        var path = _hashPath;
        try
        {
            await clipboard.CopyTextAsync(_hashValue);
            if (ReferenceEquals(ViewModel, vm) && vm.SelectedEntries.Count == 1 && vm.SelectedEntries[0].FullPath == path)
                vm.StatusText = "SHA-256 已复制";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(ViewModel, vm) && vm.SelectedEntries.Count == 1 && vm.SelectedEntries[0].FullPath == path)
                vm.StatusText = "复制哈希失败：" + ex.Message;
        }
    }
}
