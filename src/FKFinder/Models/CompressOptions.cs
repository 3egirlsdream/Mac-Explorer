namespace FKFinder.Models;

public enum ArchiveFormat { Zip, TarGz, TarBz2 }

public enum CompressionLevel { Fast, Standard, Maximum }

public class CompressOptions
{
    public ArchiveFormat Format { get; set; } = ArchiveFormat.Zip;
    public CompressionLevel Level { get; set; } = CompressionLevel.Standard;
    public string ArchiveName { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public IReadOnlyList<string> SourcePaths { get; set; } = [];
    public int? CollectionId { get; set; }
}
