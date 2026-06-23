namespace MacExplorer.Models;

public class RecognizedText
{
    public string Text { get; init; } = string.Empty;
    public float Confidence { get; init; }
    public List<string> Keywords { get; init; } = [];
}
