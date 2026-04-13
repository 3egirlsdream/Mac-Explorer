namespace FKFinder.Models;

public class AiTag
{
    public int Id { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string TagType { get; init; } = string.Empty;
    public string TagValue { get; init; } = string.Empty;
    public float Confidence { get; init; }
    public DateTime CreatedAt { get; init; }
}
