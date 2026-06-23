namespace MacExplorer.Models;

public class ImageAnalysisResult
{
    public List<DetectedFace> Faces { get; init; } = [];
    public List<RecognizedText> RecognizedTexts { get; init; } = [];
    public List<ClassificationLabel> Classifications { get; init; } = [];
    public LocationInfo? Location { get; init; }
    public PhotoDateInfo? DateInfo { get; init; }
    public string? CameraInfo { get; init; }
}
