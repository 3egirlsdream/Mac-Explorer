namespace FKFinder.Models;

public class DetectedFace
{
    public float BoundingBoxX { get; init; }
    public float BoundingBoxY { get; init; }
    public float BoundingBoxW { get; init; }
    public float BoundingBoxH { get; init; }
    public byte[]? FeaturePrint { get; init; }
}
