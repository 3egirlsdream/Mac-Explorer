using Avalonia.Headless.XUnit;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "DiskManagement")]
public sealed class DiskManagementTests
{
    internal static DiskInfo Disk(string id = "usb-1", string path = "/Volumes/USB") => new()
    {
        Identity = id, Path = path, Name = "USB", Format = "apfs", IsExternal = true, CanEject = true,
        TotalBytes = 1_000_000_000, FreeBytes = 200_000_000, AvailableBytes = 300_000_000, IsReadOnly = false
    };

    [AvaloniaFact]
    public async Task CapacityKeepsAvailableSeparateFromFreeAndPreservesUnknownFields()
    {
        var information = new Information { Disks = [Disk()] };
        using var model = new DiskManagementViewModel(information, null);
        model.SetActive(true);
        await model.RefreshAsync();
        Assert.Equal(80, model.UsedPercent);
        Assert.Equal("1 GB", DiskManagementViewModel.FormatBytes(model.SelectedDisk!.Info.TotalBytes));
        Assert.Equal("—", DiskManagementViewModel.FormatBytes(null));
        Assert.True(model.IsApfs);
        Assert.Equal(300_000_000, model.SelectedDisk.Info.AvailableBytes);
        Assert.Contains(model.TechnicalFields, field => field.Value == "—");
        Assert.False(model.CanEject);
    }

    [AvaloniaFact]
    public async Task ReplacementAtSamePathLeavesDisconnectedSelectionUntilUserChooses()
    {
        var information = new Information { Disks = [Disk()] };
        using var model = new DiskManagementViewModel(information, new Volumes());
        model.SetActive(true);
        var selected = model.SelectedDisk;
        information.Disks = [Disk("replacement")];
        await model.RefreshAsync();
        Assert.Same(selected, model.SelectedDisk);
        Assert.True(model.IsDisconnected);
        Assert.False(model.HasDetails);
        Assert.False(model.CanEject);
        Assert.False(model.CanOpen);
        model.SelectedDisk = model.Disks.Single();
        Assert.False(model.IsDisconnected);
        Assert.True(model.CanOpen);
    }

    [AvaloniaFact]
    public async Task RefreshRetainsSelectionAndOrderingWithoutAddingSharedCapacity()
    {
        var internalDisk = Disk("boot", "/") with { IsExternal = false, CanEject = false, Name = "Macintosh HD", IsReadOnly = true };
        var information = new Information { Disks = [Disk(), internalDisk] };
        using var model = new DiskManagementViewModel(information, new Volumes());
        model.SetActive(true);
        Assert.Equal("boot", model.SelectedDisk!.Info.Identity);
        Assert.False(model.CanEject);
        model.SelectedDisk = model.Disks[1];
        var selected = model.SelectedDisk;
        information.Disks = [Disk() with { AvailableBytes = 350_000_000 }, internalDisk];
        await model.RefreshAsync();
        Assert.Same(selected, model.SelectedDisk);
        Assert.Equal(350_000_000, model.SelectedDisk.Info.AvailableBytes);
        Assert.Equal(1_000_000_000, model.SelectedDisk.Info.TotalBytes);
    }

    [AvaloniaFact]
    public async Task EjectRevalidatesIdentityAndKeepsSystemFailureReason()
    {
        var information = new Information { Disks = [Disk()] };
        var volumes = new Volumes { Result = new(false, "Device is busy (16)") };
        using var model = new DiskManagementViewModel(information, volumes);
        model.SetActive(true);
        await model.EjectAsync();
        Assert.Equal(1, volumes.EjectCount);
        Assert.Equal("Device is busy (16)", model.Error);
        Assert.Single(model.Disks);
        Assert.True(model.CanEject);
        information.Disks = [Disk("replacement")];
        await model.EjectAsync();
        Assert.Equal(1, volumes.EjectCount);
        Assert.True(model.IsDisconnected);
    }

    [AvaloniaFact]
    public async Task DuplicateEjectIsDisabledAndSuccessfulEjectLeavesDisconnectedState()
    {
        var completion = new TaskCompletionSource<VolumeEjectResult>();
        var information = new Information { Disks = [Disk()] };
        var volumes = new Volumes { Pending = completion.Task };
        using var model = new DiskManagementViewModel(information, volumes);
        model.SetActive(true);
        var operation = model.EjectAsync();
        Assert.False(model.CanEject);
        await model.EjectAsync();
        Assert.Equal(1, volumes.EjectCount);
        information.Disks = [];
        completion.SetResult(new(true));
        await operation;
        Assert.Empty(model.Disks);
        Assert.True(model.IsDisconnected);
        Assert.False(model.CanEject);
    }

    [AvaloniaFact]
    public async Task BackgroundRefreshReusesFieldsAndOnlyNotifiesChangedValues()
    {
        var information = new Information { Disks = [Disk()] };
        using var model = new DiskManagementViewModel(information, null);
        model.SetActive(true);
        var row = Assert.Single(model.Disks);
        var basic = model.BasicFields.ToArray();
        var technical = model.TechnicalFields.ToArray();
        var rowChanges = new List<string?>();
        var changedFields = new List<DiskField>();
        row.PropertyChanged += (_, e) => rowChanges.Add(e.PropertyName);
        foreach (var field in basic.Concat(technical)) field.PropertyChanged += (_, _) => changedFields.Add(field);
        information.Disks = [Disk() with { }];
        await model.RefreshAsync(showProgress: false);
        Assert.Empty(rowChanges);
        Assert.Empty(changedFields);

        information.Disks = [Disk() with { AvailableBytes = 350_000_000 }];
        await model.RefreshAsync(showProgress: false);
        Assert.Same(row, model.SelectedDisk);
        Assert.Equal(basic, model.BasicFields);
        Assert.Equal(technical, model.TechnicalFields);
        Assert.Same(basic[2], Assert.Single(changedFields));
        Assert.DoesNotContain(nameof(DiskRowViewModel.Name), rowChanges);
        Assert.DoesNotContain(nameof(DiskRowViewModel.GroupTitle), rowChanges);
    }

    [AvaloniaFact]
    public async Task HiddenPageAndSupersededQueriesRejectLateResults()
    {
        var first = new TaskCompletionSource<IReadOnlyList<DiskInfo>>();
        var second = new TaskCompletionSource<IReadOnlyList<DiskInfo>>();
        var calls = 0;
        var service = new PendingInformation(() => ++calls == 1 ? first.Task : second.Task);
        using var model = new DiskManagementViewModel(service, null);
        model.SetActive(true);
        var latest = model.RefreshAsync();
        second.SetResult([Disk("new")]);
        await latest;
        first.SetResult([Disk("old")]);
        await Task.Yield();
        Assert.Equal("new", Assert.Single(model.Disks).Info.Identity);
        model.SetActive(false);
        await model.RefreshAsync();
        Assert.Equal(2, calls);

        var late = new TaskCompletionSource<IReadOnlyList<DiskInfo>>();
        using var hidden = new DiskManagementViewModel(new PendingInformation(() => late.Task), null);
        hidden.SetActive(true);
        hidden.SetActive(false);
        late.SetResult([Disk()]);
        await Task.Yield();
        Assert.Empty(hidden.Disks);
        Assert.False(hidden.IsRefreshing);
    }

    [AvaloniaFact]
    public async Task BackgroundRefreshPreservesEjectFailureUntilRetry()
    {
        var information = new Information { Disks = [Disk()] };
        var volumes = new Volumes { Result = new(false, "Device is busy") };
        using var model = new DiskManagementViewModel(information, volumes);
        model.SetActive(true);
        await model.EjectAsync();
        await model.RefreshAsync(showProgress: false);
        Assert.Equal("Device is busy", model.Error);
        information.Pending = Task.FromException<IReadOnlyList<DiskInfo>>(new IOException("query failed"));
        await model.RefreshAsync(showProgress: false);
        Assert.Equal("Device is busy", model.Error);
        information.Pending = null;
        await model.RefreshAsync();
        Assert.Equal("Device is busy", model.Error);
        volumes.Result = new(true);
        await model.EjectAsync();
        Assert.False(model.HasError);
    }

    [AvaloniaFact]
    public async Task SwitchingSelectionIncludingBackToSameDiskRejectsLateEjectError()
    {
        var information = new Information { Disks = [Disk(), Disk("other", "/Volumes/Other")] };
        var pending = new TaskCompletionSource<VolumeEjectResult>();
        var volumes = new Volumes { Pending = pending.Task };
        using var model = new DiskManagementViewModel(information, volumes);
        model.SetActive(true);
        var original = model.SelectedDisk;
        var operation = model.EjectAsync();
        model.SelectedDisk = model.Disks.Single(row => row != original);
        model.SelectedDisk = original;
        pending.SetResult(new(false, "Late failure"));
        await operation;
        Assert.False(model.HasError);
        Assert.True(model.CanEject);
    }

    [AvaloniaFact]
    public async Task RetryClearsOldOperationErrorWhilePendingAndDisposeRejectsLateFailure()
    {
        var information = new Information { Disks = [Disk()] };
        var volumes = new Volumes { Result = new(false, "First failure") };
        using var model = new DiskManagementViewModel(information, volumes);
        model.SetActive(true);
        await model.EjectAsync();
        Assert.True(model.HasError);
        var pending = new TaskCompletionSource<VolumeEjectResult>();
        volumes.Pending = pending.Task;
        var operation = model.EjectAsync();
        Assert.False(model.HasError);
        model.Dispose();
        var changes = 0;
        model.PropertyChanged += (_, _) => changes++;
        pending.SetException(new IOException("Late failure"));
        await operation;
        Assert.False(model.HasError);
        Assert.Equal(0, changes);
    }

    [AvaloniaFact]
    public async Task SwitchingDiskClearsOnlyOperationErrorAndSuccessfulQueryClearsQueryError()
    {
        var information = new Information { Disks = [Disk(), Disk("other", "/Volumes/Other")] };
        using var model = new DiskManagementViewModel(information, new Volumes { Result = new(false, "busy") });
        model.SetActive(true);
        await model.EjectAsync();
        information.Pending = Task.FromException<IReadOnlyList<DiskInfo>>(new IOException("query failed"));
        await model.RefreshAsync();
        var previous = model.SelectedDisk;
        model.SelectedDisk = model.Disks.Single(row => row != previous);
        Assert.Contains("query failed", model.Error);
        information.Pending = null;
        await model.RefreshAsync();
        Assert.False(model.HasError);
    }

    internal sealed class Information : IDiskInformationService
    {
        public IReadOnlyList<DiskInfo> Disks { get; set; } = [];
        public int Calls { get; private set; }
        public Task<IReadOnlyList<DiskInfo>>? Pending { get; set; }
        public Task<IReadOnlyList<DiskInfo>> GetMountedDisksAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Pending ?? Task.FromResult(Disks);
        }
    }

    private sealed class PendingInformation(Func<Task<IReadOnlyList<DiskInfo>>> query) : IDiskInformationService
    {
        public Task<IReadOnlyList<DiskInfo>> GetMountedDisksAsync(CancellationToken cancellationToken) => query();
    }

    internal sealed class Volumes : IVolumeMonitorService
    {
        public int EjectCount { get; private set; }
        public VolumeEjectResult Result { get; set; } = new(true);
        public Task<VolumeEjectResult>? Pending { get; set; }
        public IReadOnlyList<VolumeInfo> ExternalVolumes => [];
        public bool IsExternalVolume(string path) => path.StartsWith("/Volumes/", StringComparison.Ordinal);
        public Task<bool> EjectVolumeAsync(string volumePath) => Task.FromResult(Result.Success);
        public Task<VolumeEjectResult> EjectVolumeWithResultAsync(string volumePath) { EjectCount++; return Pending ?? Task.FromResult(Result); }
        public event Action? VolumesChanged;
        public void Notify() => VolumesChanged?.Invoke();
        public void Dispose() { }
    }
}
