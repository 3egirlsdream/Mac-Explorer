namespace MacExplorer.Models;

public sealed record NetworkServer(string Id, string Name, string Host, int Port, IReadOnlyList<string> Addresses);
