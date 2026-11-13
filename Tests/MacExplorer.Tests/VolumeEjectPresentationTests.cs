using Avalonia.Headless.XUnit;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "MacVolumeMonitorService")]
public sealed class VolumeEjectPresentationTests : FileListViewModelTestBase
{
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("System permission denied")]
    public async Task EjectFailureShowsActualReasonOrNeutralFallback(string? reason)
    {
        var files = new FakeFileService(Path.Combine(Path.GetTempPath(), "eject-presentation-fixture"));
        var volume = new VolumeInfo { Path = "/Volumes/Test", DisplayName = "Test" };
        var volumes = new Volumes(new(false, reason));
        using var vm = new FileListViewModel(new NavigationViewModel(files), new FileOpsViewModel(fileService: files),
            new SearchViewModel(), new ArchiveViewModel(), new AiViewModel(), new PinnedFoldersViewModel(),
            new SortFilterViewModel(), files, new FakeFileIndex(), new FakeFileIndexWriter(), new IndexConfiguration(),
            volumeMonitorService: volumes);
        await vm.EjectVolumeAsync(volume);
        Assert.Equal(volume.Path, volumes.RequestedPath);
        Assert.Equal(reason == null ? LocalizationText.Get("network.eject-error", "Test")
            : LocalizationText.Get("network.eject-error-detail", "Test", reason), vm.NetworkError);
        Assert.Equal(vm.NetworkError, vm.StatusText);
    }

    private sealed class Volumes(VolumeEjectResult result) : IVolumeMonitorService
    {
        public string? RequestedPath { get; private set; }
        public IReadOnlyList<VolumeInfo> ExternalVolumes => [];
        public event Action? VolumesChanged { add { } remove { } }
        public bool IsExternalVolume(string path) => false;
        public Task<bool> EjectVolumeAsync(string path) => throw new InvalidOperationException("The detailed result must be used.");
        public Task<VolumeEjectResult> EjectVolumeWithResultAsync(string path)
        { RequestedPath = path; return Task.FromResult(result); }
        public void Dispose() { }
    }
}
