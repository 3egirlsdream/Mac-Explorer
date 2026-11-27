namespace MacExplorer.Models;

public class RecognizedText
{
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; init; }
    public List<string> Keywords { get; set; } = [];
    public int? Page { get; init; }
}
