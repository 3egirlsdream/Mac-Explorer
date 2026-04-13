namespace FKFinder.Models;

public class FaceCluster
{
    public int Id { get; init; }
    public string? DisplayName { get; set; }
    public int? RepresentativeFaceId { get; set; }
    public string? RepresentativeFacePath { get; set; }
    public float BoundingBoxX { get; init; }
    public float BoundingBoxY { get; init; }
    public float BoundingBoxW { get; init; }
    public float BoundingBoxH { get; init; }
    public int FaceCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? FaceThumbnailUrl { get; set; }
}
