namespace MacExplorer.Models;

public class ArchiveProgress
{
    public bool IsActive { get; set; }
    public double Percentage { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string OperationLabel { get; set; } = string.Empty;
}
