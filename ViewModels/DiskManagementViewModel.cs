using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.ViewModels;

public sealed class DiskField : ObservableObject
{
    private readonly string _key;
    private string _label;
    private string _value = "—";

    public DiskField(string key) { _key = key; _label = LocalizationText.Get(key); }
    public string Label => _label;
    public string Value => _value;

    internal void Update(string? value)
    {
        SetProperty(ref _label, LocalizationText.Get(_key), nameof(Label));
        SetProperty(ref _value, string.IsNullOrWhiteSpace(value) ? "—" : value, nameof(Value));
    }
}

public sealed class DiskRowViewModel : ObservableObject
{
    public DiskRowViewModel(DiskInfo info) => _info = info;
    private DiskInfo _info;
    private string _groupTitle = "";
    public DiskInfo Info => _info;
    public string Name => Info.Name;
    public string GroupTitle => _groupTitle;
    public bool HasGroupTitle => GroupTitle.Length > 0;
    public string IconData => Assets.Icons.ExternalDrive;
    public string CapacityText => LocalizationText.Get("disk.available-of", DiskManagementViewModel.FormatBytes(Info.AvailableBytes), DiskManagementViewModel.FormatBytes(Info.TotalBytes));
    public double UsedPercent => Info.UsedPercent;
    public bool HasCapacity => Info.TotalBytes is > 0 && Info.FreeBytes.HasValue;
    public string TypeLabel => GetTypeLabel(Info);
    internal static string GetTypeLabel(DiskInfo info) => LocalizationText.Get(info.IsDiskImage ? "disk.image" : info.IsExternal ? "disk.external" : "disk.internal");
    public string FormatText => Info.Format?.ToLowerInvariant() switch
    {
        "exfat" => "exFAT",
        "hfs" => "HFS+",
        _ => Info.Format?.ToUpperInvariant() ?? "—"
    };
    public void Update(DiskInfo info, string groupTitle)
    {
        var previous = _info;
        var previousGroup = _groupTitle;
        _info = info;
        _groupTitle = groupTitle;
        if (previous != info) OnPropertyChanged(nameof(Info));
        if (previous.Name != info.Name) OnPropertyChanged(nameof(Name));
        if (previous.TotalBytes != info.TotalBytes || previous.AvailableBytes != info.AvailableBytes)
            OnPropertyChanged(nameof(CapacityText));
        if (previous.TotalBytes != info.TotalBytes || previous.FreeBytes != info.FreeBytes)
        {
            OnPropertyChanged(nameof(UsedPercent));
            OnPropertyChanged(nameof(HasCapacity));
        }
        if (previous.IsDiskImage != info.IsDiskImage || previous.IsExternal != info.IsExternal)
            OnPropertyChanged(nameof(TypeLabel));
        if (previous.Format != info.Format) OnPropertyChanged(nameof(FormatText));
        if (previousGroup != groupTitle)
        {
            OnPropertyChanged(nameof(GroupTitle));
            if ((previousGroup.Length > 0) != HasGroupTitle) OnPropertyChanged(nameof(HasGroupTitle));
        }
    }

    public void RefreshLanguage(string groupTitle)
    {
        Update(Info, groupTitle);
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(CapacityText));
    }
}

public sealed class DiskManagementViewModel : ObservableObject, IDisposable
{
    private readonly IDiskInformationService _information;
    private readonly IVolumeMonitorService? _volumes;
    private CancellationTokenSource? _refreshCancellation;
    private DiskRowViewModel? _selectedDisk;
    private bool _active;
    private bool _disposed;
    private bool _isRefreshing;
    private bool _isLoading;
    private bool _isDisconnected;
    private string _error = "";
    private string _queryError = "";
    private string _operationError = "";
    private long _selectionVersion;
    private string? _ejectingIdentity;

    public DiskManagementViewModel(IDiskInformationService information, IVolumeMonitorService? volumes)
    {
        _information = information;
        _volumes = volumes;
    }

    public ObservableCollection<DiskRowViewModel> Disks { get; } = [];
    public DiskRowViewModel? SelectedDisk
    {
        get => _selectedDisk;
        set
        {
            // A disappearing item must leave an explicit disconnected state, not select another disk.
            if (value == null || !SetProperty(ref _selectedDisk, value)) return;
            _selectionVersion++;
            IsDisconnected = false;
            _operationError = "";
            UpdateError();
            NotifyDetails();
        }
    }
    public bool IsRefreshing { get => _isRefreshing; private set => SetProperty(ref _isRefreshing, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsDisconnected
    {
        get => _isDisconnected;
        private set { if (SetProperty(ref _isDisconnected, value)) NotifyDetails(); }
    }
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    private void UpdateError() => Error = _operationError.Length > 0 ? _operationError : _queryError;
    public bool HasDetails => SelectedDisk != null && !IsDisconnected;
    public bool HasSelection => SelectedDisk != null;
    public bool IsEmpty => Disks.Count == 0 && SelectedDisk == null && !IsRefreshing;
    public bool CanOpen => HasDetails;
    public bool CanEject => HasDetails && SelectedDisk!.Info.CanEject && _volumes != null && _ejectingIdentity == null;
    public bool IsEjecting => _ejectingIdentity != null;
    public string Name => SelectedDisk?.Name ?? "";
    public string IconData => SelectedDisk?.IconData ?? Assets.Icons.ExternalDrive;
    public string Status => SelectedDisk == null ? "" : string.Join(" · ", new[] { SelectedDisk.TypeLabel,
        SelectedDisk.FormatText, SelectedDisk.Info.IsReadOnly is { } readOnly
            ? LocalizationText.Get(readOnly ? "disk.read-only" : "disk.read-write") : null }.Where(text => !string.IsNullOrEmpty(text)));
    public string CapacityText => SelectedDisk?.CapacityText ?? "";
    public string FreeText => LocalizationText.Get("disk.free-value", FormatBytes(SelectedDisk?.Info.FreeBytes));
    public bool HasCapacity => HasDetails && SelectedDisk!.HasCapacity;
    public double UsedPercent => SelectedDisk?.UsedPercent ?? 0;
    public bool IsApfs => HasDetails && SelectedDisk!.Info.IsApfs;
    public IReadOnlyList<DiskField> BasicFields { get; } =
    [
        new("disk.format"), new("disk.total"), new("disk.available"), new("disk.free"),
        new("disk.mount-path"), new("disk.access"), new("disk.connection")
    ];
    public IReadOnlyList<DiskField> TechnicalFields { get; } =
    [
        new("disk.device"), new("disk.uuid"), new("disk.container"), new("disk.model")
    ];

    private void UpdateFields()
    {
        if (SelectedDisk is not { } row) return;
        BasicFields[0].Update(row.FormatText);
        BasicFields[1].Update(FormatBytes(row.Info.TotalBytes));
        BasicFields[2].Update(FormatBytes(row.Info.AvailableBytes));
        BasicFields[3].Update(FormatBytes(row.Info.FreeBytes));
        BasicFields[4].Update(row.Info.Path);
        BasicFields[5].Update(row.Info.IsReadOnly is { } readOnly ? LocalizationText.Get(readOnly ? "disk.read-only" : "disk.read-write") : null);
        BasicFields[6].Update(row.Info.Connection);
        TechnicalFields[0].Update(row.Info.DeviceIdentifier);
        TechnicalFields[1].Update(row.Info.VolumeUuid);
        TechnicalFields[2].Update(row.Info.ContainerIdentifier);
        TechnicalFields[3].Update(row.Info.DeviceName);
    }

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (active) _ = RefreshAsync(showProgress: Disks.Count == 0 && SelectedDisk == null);
        else { _refreshCancellation?.Cancel(); IsRefreshing = false; IsLoading = false; }
    }

    public async Task RefreshAsync(bool showProgress = true)
    {
        if (!_active || _disposed) return;
        _refreshCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        IsRefreshing = true;
        IsLoading = showProgress;
        try
        {
            var disks = await _information.GetMountedDisksAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || !_active || _disposed) return;
            ApplyDisks(disks);
            _queryError = "";
            UpdateError();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && _active && !_disposed)
            {
                _queryError = LocalizationText.Get("disk.read-error-detail", ex.Message);
                UpdateError();
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                IsRefreshing = false;
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    private void ApplyDisks(IReadOnlyList<DiskInfo> disks)
    {
        var previousSelection = SelectedDisk?.Info;
        var ordered = disks.OrderBy(disk => disk.IsDiskImage ? 2 : disk.IsExternal ? 1 : 0)
            .ThenBy(disk => disk.Path == "/" ? 0 : 1).ThenBy(disk => disk.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var identities = ordered.Select(disk => disk.Identity).ToHashSet(StringComparer.Ordinal);
        if (SelectedDisk != null) IsDisconnected = !identities.Contains(SelectedDisk.Info.Identity);
        foreach (var missing in Disks.Where(row => !identities.Contains(row.Info.Identity)).ToArray()) Disks.Remove(missing);
        string? previousGroup = null;
        for (var index = 0; index < ordered.Length; index++)
        {
            var disk = ordered[index];
            var row = Disks.FirstOrDefault(item => item.Info.Identity == disk.Identity);
            if (row == null) { row = new DiskRowViewModel(disk); Disks.Insert(index, row); }
            else if (Disks.IndexOf(row) != index) Disks.Move(Disks.IndexOf(row), index);
            var group = DiskRowViewModel.GetTypeLabel(disk);
            row.Update(disk, group != previousGroup ? group : "");
            previousGroup = group;
        }
        if (SelectedDisk == null) SelectedDisk = Disks.FirstOrDefault();
        else if (!IsDisconnected && Disks.FirstOrDefault(row => row.Info.Identity == SelectedDisk.Info.Identity) is { } selected)
            SelectedDisk = selected;
        if (previousSelection != SelectedDisk?.Info) NotifyDetails();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task EjectAsync()
    {
        if (_disposed || !CanEject || SelectedDisk is not { Info: var disk } || _volumes == null) return;
        var selectionVersion = _selectionVersion;
        _operationError = "";
        UpdateError();
        _ejectingIdentity = disk.Identity;
        NotifyDetails();
        try
        {
            // Revalidate identity immediately before an operation; mount paths can be reused.
            var current = await _information.GetMountedDisksAsync(CancellationToken.None);
            if (_disposed) return;
            if (!current.Any(item => item.Identity == disk.Identity && item.Path == disk.Path && item.CanEject))
            {
                if (IsCurrentOperation()) IsDisconnected = true;
                return;
            }
            var result = await _volumes.EjectVolumeWithResultAsync(disk.Path);
            if (!result.Success)
                SetOperationError(string.IsNullOrWhiteSpace(result.ErrorMessage) ? LocalizationText.Get("disk.eject-error") : result.ErrorMessage);
            else await RefreshAsync();
        }
        catch (Exception ex) { SetOperationError(LocalizationText.Get("disk.operation-error", ex.Message)); }
        finally { _ejectingIdentity = null; if (!_disposed) NotifyDetails(); }

        bool IsCurrentOperation() => !_disposed && selectionVersion == _selectionVersion
            && SelectedDisk?.Info.Identity == disk.Identity;
        void SetOperationError(string message)
        {
            if (!IsCurrentOperation()) return;
            _operationError = message;
            UpdateError();
        }
    }

    public string CopyInformation() => string.Join(Environment.NewLine,
        new[] { Name }.Concat(BasicFields.Concat(TechnicalFields).Select(field => $"{field.Label}: {field.Value}")));

    public void RefreshLanguage()
    {
        string? previousGroup = null;
        foreach (var row in Disks)
        {
            var group = row.TypeLabel;
            row.RefreshLanguage(group != previousGroup ? group : "");
            previousGroup = group;
        }
        NotifyDetails();
    }

    private void NotifyDetails()
    {
        UpdateFields();
        foreach (var name in new[] { nameof(HasDetails), nameof(HasSelection), nameof(CanOpen), nameof(CanEject), nameof(IsEjecting), nameof(Name),
            nameof(IconData), nameof(Status), nameof(CapacityText), nameof(FreeText), nameof(HasCapacity), nameof(UsedPercent),
            nameof(IsApfs) }) OnPropertyChanged(name);
    }

    internal static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue || bytes < 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return value.ToString("0.##", LocalizationService.Current?.Culture ?? System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public void Dispose()
    {
        SetActive(false);
        _disposed = true;
    }
}
