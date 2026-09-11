namespace MacExplorer.Models;

public class FileOperationProgress
{
    public double Percentage { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public int SkippedCount { get; set; }
}
