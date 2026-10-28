using MacExplorer.Services.Impl;

namespace MacExplorer.Services;

public sealed record WebDavAddress(Uri Uri)
{
    public static WebDavAddress Parse(string address)
    {
        var input = address.Trim();
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        if (input.Any(char.IsControl) || input.Contains('\\')
            || !System.Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(LocalizationText.Get("network.webdav-invalid-address"));
        // Do not silently normalize a pasted parent-directory segment into a different mount root.
        var pathStart = input.IndexOf('/', input.IndexOf("://", StringComparison.Ordinal) + 3);
        if (pathStart >= 0 && input[pathStart..].Split('/').Any(segment =>
            System.Uri.UnescapeDataString(segment) is "." or ".." || System.Uri.UnescapeDataString(segment).Any(char.IsControl)))
            throw new ArgumentException(LocalizationText.Get("network.webdav-invalid-address"));
        return new(uri);
    }

    public bool Matches(string? source)
    {
        if (!System.Uri.TryCreate(source, UriKind.Absolute, out var other)) return false;
        return Uri.Scheme == other.Scheme && Uri.Port == other.Port
            && string.Equals(Uri.IdnHost.TrimEnd('.'), other.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/'),
                other.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/'), StringComparison.Ordinal);
    }
}
