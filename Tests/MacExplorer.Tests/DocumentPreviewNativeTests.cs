using System.Diagnostics;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Preview")]
public sealed class DocumentPreviewNativeTests
{
    [Fact(Explicit = true)]
    public async Task OfficeFillsTheHostAndNativePreviewsUseSharedScrollbars()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS document previews.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "MacExplorer.csproj"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var root = Directory.CreateTempSubdirectory("document-preview-native-");
        try
        {
            var office = Path.Combine(root.FullName, "fixture.docx");
            using (var document = WordprocessingDocument.Create(office, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
                document.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("Office preview fixture")))));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var executable = Path.Combine(root.FullName, "DocumentPreviewSmoke");
            await Run("/usr/bin/clang++", ["-fobjc-arc", Path.Combine(repository.FullName, "Tools/Testing/DocumentPreviewSmoke.mm"),
                "-framework", "AppKit", "-framework", "Quartz", "-framework", "WebKit", "-o", executable], timeout.Token);
            var output = await Run("/bin/bash", [Path.Combine(repository.FullName, "Tools/Testing/run-isolated.sh"), executable,
                Path.Combine(AppContext.BaseDirectory, "libMacExplorerNativeDrag.dylib"), office], timeout.Token);
            Assert.Contains("shared scrollbars and cleanup", output);
        }
        finally { root.Delete(true); }
    }

    private static async Task<string> Run(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var result = await output;
        Assert.True(process.ExitCode == 0, result + await error);
        return result;
    }
}
