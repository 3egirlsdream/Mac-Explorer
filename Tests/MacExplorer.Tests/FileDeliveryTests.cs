using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FileDeliveryTests
{
    [Fact]
    public void DefaultsAreCreatedOnceAndRemovingAllEntriesSurvivesRestart()
    {
        using var fixture = new Fixture();
        using (var service = new FileDeliveryService(fixture.Settings, fixture.Tags))
        {
            Assert.True(service.Enabled);
            Assert.Equal(new[] { "下载", "桌面" }, service.Preferences.Entries.Select(e => e.Name));
            foreach (var entry in service.Preferences.Entries.ToArray()) service.Remove(entry.Id);
            service.Enabled = false;
        }
        using var restarted = new FileDeliveryService(fixture.Settings, fixture.Tags);
        Assert.Empty(restarted.Preferences.Entries);
        Assert.False(restarted.Enabled);
    }

    [Fact]
    public void PositionsOrderAndSelectedEntryPersistWithoutMovingFiles()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, "入口");
        var child = Path.Combine(directory, "子目录");
        Directory.CreateDirectory(child);
        var file = Path.Combine(child, "保留.txt");
        File.WriteAllText(file, "keep");
        string id;
        using (var service = new FileDeliveryService(fixture.Settings, fixture.Tags))
        {
            service.AddFolder(directory);
            service.AddFolder(directory + "/");
            var entry = Assert.Single(service.Preferences.Entries, e => e.Location == directory);
            id = entry.Id;
            service.Move(id, -1);
            service.Move(id, -1);
            service.SavePosition(id, child, new(file, -7, 37), 37);
        }
        using var restarted = new FileDeliveryService(fixture.Settings, fixture.Tags);
        var saved = restarted.Preferences.Entries[0];
        Assert.Equal(id, saved.Id);
        Assert.Equal(id, restarted.Preferences.SelectedId);
        Assert.Equal(child, FileDeliveryService.ResolveLocation(saved));
        Assert.Equal(file, saved.ScrollAnchor?.ItemPath);
        Assert.Equal(37, saved.ScrollOffset);
        Assert.Equal(directory, FileDeliveryService.ResolveLocation(saved with { CurrentLocation = child + "/missing" }));
        restarted.Remove(id);
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public async Task ExistingTagReferencesFollowRenameAndDelete()
    {
        using var fixture = new Fixture();
        using var service = new FileDeliveryService(fixture.Settings, fixture.Tags);
        var tag = await fixture.Tags.CreateTagAsync("素材");
        service.AddTag(tag);
        var entry = service.Preferences.Entries.Last();
        service.SavePosition(entry.Id, tag.VirtualPath, null, 0);
        await fixture.Tags.RenameTagAsync(tag, "设计素材");
        var renamed = service.Preferences.Entries.Last();
        Assert.Equal(entry.Id, renamed.Id);
        Assert.Equal("设计素材", renamed.Name);
        Assert.Equal((tag with { Name = "设计素材" }).VirtualPath, renamed.CurrentLocation);
        await fixture.Tags.DeleteTagAsync(tag with { Name = "设计素材" });
        Assert.DoesNotContain(service.Preferences.Entries, e => e.Id == entry.Id);
    }

    [Fact]
    public void PanelViewPreferencesDoNotOverwriteMainWindowPreferences()
    {
        using var fixture = new Fixture();
        fixture.Settings.Set("ViewMode", ViewMode.Grid);
        fixture.Settings.Set("SortField", SortField.Name);
        var panelSettings = new FileDeliverySettings(fixture.Settings);
        var panel = new SortFilterViewModel(panelSettings);
        Assert.Equal(ViewMode.List, panel.ViewMode);
        panel.SortField = SortField.Modified;
        panel.ViewMode = ViewMode.Grid;
        Assert.Equal(SortField.Name, fixture.Settings.Get("SortField", SortField.Size));
        Assert.Equal(SortField.Modified, new SortFilterViewModel(panelSettings).SortField);
        Assert.DoesNotContain(panelSettings.GetAll().Keys, k => k.StartsWith("file_delivery_view_"));
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("FileDelivery_").FullName;
        public SettingsService Settings { get; }
        public FileTagService Tags { get; }
        public Fixture()
        {
            var database = new DatabaseConnectionFactory(Path.Combine(Root, "test.db"));
            using var connection = database.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
            command.ExecuteNonQuery();
            Settings = new SettingsService(database);
            Tags = new FileTagService(database);
        }
        public void Dispose() { Tags.Dispose(); Settings.Dispose(); Directory.Delete(Root, true); }
    }
}
