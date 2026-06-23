namespace MacExplorer.Models;

public class ClassificationLabel
{
    public string Identifier { get; init; } = string.Empty;
    public string TagType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public float Confidence { get; init; }
}
