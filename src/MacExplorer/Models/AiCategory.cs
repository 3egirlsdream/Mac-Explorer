namespace MacExplorer.Models;

public class AiCategory
{
    public string TagType { get; init; } = string.Empty;
    public string TagValue { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public int? FaceClusterId { get; init; }
}
