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
        var root = Directory.CreateTempSubdirectory("document-preview-native-");
        try
        {
            var office = Path.Combine(root.FullName, "fixture.docx");
            using (var document = WordprocessingDocument.Create(office, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
                document.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("Office preview fixture")))));
            var output = await NativeHarnessRunner.CompileAndRunAsync(
                "Tools/Testing/DocumentPreviewSmoke.mm",
                ["-fobjc-arc", "-framework", "AppKit", "-framework", "Quartz", "-framework", "WebKit"],
                runArguments: [Path.Combine(AppContext.BaseDirectory, "libMacExplorerNativeDrag.dylib"), office],
                workingDirectory: root.FullName);
            Assert.Contains("shared scrollbars and cleanup", output);
        }
        finally { root.Delete(true); }
    }
}
