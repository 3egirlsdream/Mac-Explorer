namespace MacExplorer.Models;

public class ImageAnalysisResult
{
    public List<DetectedFace> Faces { get; set; } = [];
    public List<RecognizedText> RecognizedTexts { get; set; } = [];
    public List<ClassificationLabel> Classifications { get; set; } = [];
    public LocationInfo? Location { get; init; }
    public PhotoDateInfo? DateInfo { get; init; }
    public string? CameraInfo { get; init; }
}
