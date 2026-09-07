using System.Collections.ObjectModel;
using MacExplorer.Models;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

public class FileListPresentationUpdateTests
{
    [Fact]
    public void SelectionAndCutDoNotInvalidateIcons()
    {
        var entry = Entry("/a");
        var notifications = new List<string?>();
        entry.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        entry.IsSelected = true;
        entry.IsCut = true;
        Assert.Equal(new[] { nameof(FileSystemEntry.IsSelected), nameof(FileSystemEntry.IsCut) }, notifications);
    }

    [Fact]
    public void ThumbnailChangeOnlyInvalidatesGridDescriptor()
    {
        var entry = Entry("/a");
        var notifications = new List<string?>();
        entry.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        entry.ThumbnailUrl = "/cache/a.png";
        Assert.Contains(nameof(FileSystemEntry.ThumbnailUrl), notifications);
        Assert.Contains(nameof(FileSystemEntry.GridIconSource), notifications);
        Assert.DoesNotContain(nameof(FileSystemEntry.DetailsIconSource), notifications);
        Assert.DoesNotContain(notifications, string.IsNullOrEmpty);
    }

    [Fact]
    public void NativeIconRefreshNeverUsesWildcardPropertyNotification()
    {
        var entry = Entry("/a");
        var notifications = new List<string?>();
        entry.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        entry.RaiseIconBindingChanged();
        Assert.Equal(new[] { nameof(FileSystemEntry.DetailsIconSource), nameof(FileSystemEntry.GridIconSource) }, notifications);
    }

    [Fact]
    public void DetailsDescriptorNeverUsesThumbnailPath()
    {
        var entry = Entry("/a");
        entry.IconUrl = "data:icon";
        entry.ThumbnailUrl = "/cache/thumbnail.png";
        Assert.Equal("data:icon", entry.DetailsIconSource.CachedImagePath);
        Assert.Equal("/cache/thumbnail.png", entry.GridIconSource.CachedImagePath);
    }

    [Fact]
    public void GitDeltaPreservesCollectionItemAndSelectionIdentity()
    {
        var original = Entry("/repo/a");
        original.IsSelected = true;
        var current = new ObservableCollection<FileSystemEntry>([original]);
        var notifications = 0;
        current.CollectionChanged += (_, _) => notifications++;
        var resolved = Entry("/repo/a");
        resolved.GitStatus = GitFileStatus.Modified;
        var changed = FileGitPresentationUpdater.Apply(current, [resolved]);
        Assert.Equal(1, changed);
        Assert.Same(original, current[0]);
        Assert.True(original.IsSelected);
        Assert.Equal(GitFileStatus.Modified, original.GitStatus);
        Assert.True(original.HasGitBadge);
        Assert.Equal("M", original.GitBadgeText);
        Assert.Equal(0, notifications);
        Assert.Equal(0, FileGitPresentationUpdater.Apply(current, [resolved]));
    }

    [Fact]
    public void GitDeltaIgnoresOtherPathsAndChangedMetadata()
    {
        var original = Entry("/repo/a");
        var stale = new FileSystemEntry { FullPath = original.FullPath, LastModified = original.LastModified.AddSeconds(-1), GitStatus = GitFileStatus.Modified };
        var other = Entry("/repo/b");
        other.GitStatus = GitFileStatus.Modified;
        Assert.Equal(0, FileGitPresentationUpdater.Apply([original], [stale, other]));
    }

    [Fact]
    public void GitDerivedPropertiesNotifyWithoutIconInvalidation()
    {
        var entry = Entry("/repo/a");
        var notifications = new List<string?>();
        entry.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        entry.GitStatus = GitFileStatus.Added;
        Assert.Contains(nameof(FileSystemEntry.GitBadgeText), notifications);
        Assert.Contains(nameof(FileSystemEntry.GitBadgeColor), notifications);
        Assert.Contains(nameof(FileSystemEntry.HasGitBadge), notifications);
        Assert.DoesNotContain(nameof(FileSystemEntry.GridIconSource), notifications);
    }

    private static FileSystemEntry Entry(string path) => new()
    {
        FullPath = path, Name = Path.GetFileName(path), LastModified = new DateTime(2026, 9, 3)
    };
}
