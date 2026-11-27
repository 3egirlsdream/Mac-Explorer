namespace MacExplorer.Models;

public class PhotoDateInfo
{
    public DateTime TakenAt { get; init; }
    public string YearMonth { get; set; } = string.Empty;
    public string Day { get; set; } = string.Empty;
}
