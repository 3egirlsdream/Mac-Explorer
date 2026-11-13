namespace MacExplorer.Models;

public sealed record VolumeEjectResult(bool Success, string? ErrorMessage = null);
