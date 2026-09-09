using System.Collections.ObjectModel;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FileColumnFilterTests
{
    [Fact]
    public void SelectionsUnionWithinColumnAndIntersectWithOtherColumnsAndFilename()
    {
        var vm = new SortFilterViewModel();
        vm.SetRawEntries([File("Alpha-report.txt", 2048), File("Echo-report.txt", 2048),
            File("Zulu-report.txt", 2048), File("Alpha-report.png", 2048), File("Alpha-note.txt", 2048), File("Alpha-report-large.txt", 2000000)]);
        vm.ColumnFilters = FileListFilterState.Empty.Set(SortField.Name, "A–D", true)
            .Set(SortField.Name, "E–H", true).Set(SortField.Type, ".txt", true)
            .Set(SortField.Size, "小于 1 MB", true) with { FileName = "REPORT" };
        Assert.Equal(["Alpha-report.txt", "Echo-report.txt"], Apply(vm));
        vm.ColumnFilters = vm.ColumnFilters.Set(SortField.Name, "A–D", false);
        Assert.Equal(["Echo-report.txt"], Apply(vm));
        vm.ColumnFilters = FileListFilterState.Empty;
        Assert.Equal(6, Apply(vm).Length);
    }

    [Fact]
    public void FacetsRetainAlternativesAndSelectedZeroResultsWhileRespectingOtherFilters()
    {
        var vm = new SortFilterViewModel();
        vm.SetRawEntries([File("Alpha.TXT", 1), File("Echo.png", 1), File("Zulu.txt", 1), File(".hidden.txt", 1)]);
        vm.ColumnFilters = FileListFilterState.Empty.Set(SortField.Name, "A–D", true)
            .Set(SortField.Type, ".txt", true).Set(SortField.Type, ".missing", true);
        var names = vm.GetColumnFilterOptions(SortField.Name);
        Assert.Equal(1, names.Single(o => o.Key == "U–Z").Count);
        Assert.Equal(0, names.Single(o => o.Key == "E–H").Count);
        var types = vm.GetColumnFilterOptions(SortField.Type);
        Assert.Equal(1, types.Single(o => o.Key == ".txt").Count);
        Assert.True(types.Single(o => o.Key == ".missing").IsSelected);
        Assert.Equal(0, types.Single(o => o.Key == ".png").Count);
    }

    [Theory]
    [InlineData("a.txt", "A–D")]
    [InlineData("Zulu.txt", "U–Z")]
    [InlineData("9.txt", "0–9")]
    [InlineData("中文.txt", "中文")]
    [InlineData("_file", "其他")]
    public void NameRangesCoverAsciiChineseDigitsAndOtherCharacters(string name, string bucket)
    {
        var vm = new SortFilterViewModel();
        vm.SetRawEntries([File(name, 1)]);
        Assert.Equal(1, vm.GetColumnFilterOptions(SortField.Name).Single(o => o.Key == bucket).Count);
    }

    [Fact]
    public void DateBucketsUseCalendarBoundariesAndMatchGroupedRows()
    {
        var today = new DateTime(2026, 9, 9);
        var entries = new[] { File("today", 1, today), File("yesterday", 1, today.AddSeconds(-1)),
            File("week", 1, today.AddDays(-6)), File("month", 1, today.AddDays(-29)),
            File("quarter", 1, today.AddMonths(-3)), File("year", 1, new DateTime(2026, 1, 1)),
            File("old", 1, new DateTime(2025, 12, 31)), File("future", 1, today.AddDays(1)) };
        var query = new FileListQuery(SortField.Name, true, GroupField.Modified, true, true, true) { FilterDate = today };
        var snapshot = new FileListSnapshot(1, query, entries, [], [], true);
        var all = FileListDataPipeline.Reproject(snapshot, query);
        Assert.Equal(["未来", "今天", "昨天", "最近7天", "最近30天", "最近3个月", "今年更早", "更早"], all.Groups.Select(g => g.Name));
        var filtered = FileListDataPipeline.Reproject(snapshot, query with
        {
            ColumnFilters = FileListFilterState.Empty.Set(SortField.Modified, "昨天", true)
        });
        Assert.Equal("yesterday", Assert.Single(filtered.Entries).Name);
        Assert.Equal("昨天", Assert.Single(filtered.Groups).Name);
    }

    [Theory]
    [InlineData(0, "空文件")]
    [InlineData(1023, "小于 1 KB")]
    [InlineData(1024, "小于 1 MB")]
    [InlineData(1048576, "1-100 MB")]
    [InlineData(104857600, "100 MB-1 GB")]
    [InlineData(1073741824, "大于 1 GB")]
    public void SizeBoundariesHaveExactlyOneBucket(long size, string bucket)
    {
        var vm = new SortFilterViewModel();
        vm.SetRawEntries([File("file", size)]);
        Assert.Equal(bucket, Assert.Single(vm.GetColumnFilterOptions(SortField.Size), o => o.Count == 1).Key);
    }

    [Fact]
    public void DirectoriesAndExtensionlessFilesHaveSeparateTypeAndSizeChoices()
    {
        var directory = new FileSystemEntry { Name = "Folder", FullPath = "/test/Folder", IsDirectory = true };
        var vm = new SortFilterViewModel();
        vm.SetRawEntries([directory, File("README", 0)]);
        Assert.Equal(["文件夹", "无后缀"], vm.GetColumnFilterOptions(SortField.Type).Select(o => o.Key));
        vm.ColumnFilters = FileListFilterState.Empty.Set(SortField.Size, "空文件", true);
        Assert.Equal(["README"], Apply(vm));
        vm.ColumnFilters = FileListFilterState.Empty.Set(SortField.Size, "文件夹", true);
        Assert.Equal(["Folder"], Apply(vm));
    }

    [Fact]
    public async Task StreamingAndReprojectionUseDetachedFiltersAndKeepRawEntries()
    {
        var vm = new SortFilterViewModel();
        vm.ColumnFilters = FileListFilterState.Empty.Set(SortField.Type, ".txt", true);
        var captured = vm.CaptureQuery();
        Assert.Equal(captured, vm.CaptureQuery());
        vm.ColumnFilters = FileListFilterState.Empty.Set(SortField.Type, ".png", true);
        FileListSnapshot? snapshot = null;
        await foreach (var item in new FileListDataPipeline().LoadAsync(Batches(), captured)) snapshot = item;
        Assert.Equal("Alpha.txt", Assert.Single(snapshot!.Entries).Name);
        Assert.Equal(2, snapshot.RawEntries.Count);
        Assert.Equal("Beta.png", Assert.Single(FileListDataPipeline.Reproject(snapshot, vm.CaptureQuery()).Entries).Name);
        Assert.Equal(2, FileListDataPipeline.Reproject(snapshot, captured with { ColumnFilters = FileListFilterState.Empty }).Entries.Count);

        static async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> Batches()
        {
            yield return new[] { File("Alpha.txt", 1), File("Beta.png", 2) };
            await Task.CompletedTask;
        }
    }

    private static string[] Apply(SortFilterViewModel vm)
    {
        ObservableCollection<FileSystemEntry> result = [];
        vm.ApplySortAndGroup(entries => result = entries);
        return result.Select(entry => entry.Name).ToArray();
    }

    internal static FileSystemEntry File(string name, long size, DateTime? modified = null) => new()
    {
        FullPath = "/test/" + name, Name = name, Size = size,
        Extension = Path.GetExtension(name), LastModified = modified ?? DateTime.Today
    };
}
