namespace MacExplorer.Models;

public class ClassificationLabel
{
    public string Identifier { get; set; } = string.Empty;
    public string TagType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public float Confidence { get; init; }
}
