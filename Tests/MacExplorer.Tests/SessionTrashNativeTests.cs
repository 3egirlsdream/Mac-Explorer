using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "NativeTrash")]
public sealed class SessionTrashNativeTests
{
    [Fact(Explicit = true)]
    public async Task SameNamedTrashedFilesRestoreByRecordedLocation()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("Requires macOS Trash API.");
        var root = Path.Combine(Path.GetTempPath(), "fk-trash-collision-" + Guid.NewGuid().ToString("N"));
        var firstDirectory = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        var name = "fk-task-" + Guid.NewGuid().ToString("N") + ".txt";
        var first = Path.Combine(firstDirectory, name); var second = Path.Combine(secondDirectory, name);
        string? firstTrash = null; string? secondTrash = null;
        try
        {
            await File.WriteAllTextAsync(first, "first"); await File.WriteAllTextAsync(second, "second");
            var files = new MacFileService(); var history = new FileOperationHistoryService(files);
            firstTrash = await files.TrashWithResultAsync(first); await history.RecordTrashAsync(first, firstTrash!);
            var id = history.Records[0].Id;
            secondTrash = await files.TrashWithResultAsync(second); await history.RecordTrashAsync(second, secondTrash!);
            Assert.NotEqual(firstTrash, secondTrash);
            var undo = await history.UndoAsync(id); Assert.True(undo.Success, undo.Message);
            Assert.Equal("first", await File.ReadAllTextAsync(first)); Assert.False(File.Exists(second));
            Assert.Equal("second", await File.ReadAllTextAsync(secondTrash!));
            Assert.True(await history.UndoLastAsync()); Assert.Equal("second", await File.ReadAllTextAsync(second));
        }
        finally
        {
            if (firstTrash != null && File.Exists(firstTrash)) File.Delete(firstTrash);
            if (secondTrash != null && File.Exists(secondTrash)) File.Delete(secondTrash);
            Directory.Delete(root, true);
        }
    }

    [Fact(Explicit = true)]
    public async Task NativeTrashResultRestoresTheExactItemAndOriginalName()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("Requires macOS Trash API.");
        var root = Path.Combine(Path.GetTempPath(), "fk-trash-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "fk-task-" + Guid.NewGuid().ToString("N") + ".txt");
        string? trashed = null;
        try
        {
            await File.WriteAllTextAsync(original, "isolated task history fixture");
            var files = new MacFileService();
            trashed = await files.TrashWithResultAsync(original);
            Assert.NotNull(trashed); Assert.True(File.Exists(trashed)); Assert.False(File.Exists(original));
            var history = new FileOperationHistoryService(files);
            await history.RecordTrashAsync(original, trashed!);
            Assert.True(await history.UndoLastAsync());
            Assert.Equal("isolated task history fixture", await File.ReadAllTextAsync(original));
            Assert.False(File.Exists(trashed));
        }
        finally
        {
            if (trashed != null && File.Exists(trashed)) File.Delete(trashed);
            Directory.Delete(root, true);
        }
    }
}
