using FKFinder.Models;

namespace FKFinder.Services;

public static class AiPathHelper
{
    private const string Prefix = "__ai:";

    public static bool IsAiPath(string path) =>
        path.StartsWith(Prefix, StringComparison.Ordinal);

    public static string GetTopLevelPath(AiViewMode mode) => mode switch
    {
        AiViewMode.People => $"{Prefix}people",
        AiViewMode.Categories => $"{Prefix}categories",
        AiViewMode.Locations => $"{Prefix}locations",
        AiViewMode.Dates => $"{Prefix}dates",
        AiViewMode.TextSearch => $"{Prefix}textsearch",
        _ => $"{Prefix}people"
    };

    public static AiPathInfo Parse(string sentinelPath)
    {
        var payload = sentinelPath[Prefix.Length..];

        // Top-level views
        return payload switch
        {
            "people" => new AiPathInfo(true, AiViewMode.People),
            "categories" => new AiPathInfo(true, AiViewMode.Categories),
            "locations" => new AiPathInfo(true, AiViewMode.Locations),
            "dates" => new AiPathInfo(true, AiViewMode.Dates),
            "textsearch" => new AiPathInfo(true, AiViewMode.TextSearch),
            _ => ParseDetail(payload)
        };
    }

    public static string GetParentPath(string sentinelPath)
    {
        var info = Parse(sentinelPath);
        if (info.IsTopLevel) return "";
        return GetTopLevelPath(info.Mode);
    }

    public static string GetModeName(AiViewMode mode) => mode switch
    {
        AiViewMode.People => "\u4eba\u7269",
        AiViewMode.Categories => "\u5206\u7c7b",
        AiViewMode.Locations => "\u5730\u70b9",
        AiViewMode.Dates => "\u65e5\u671f",
        AiViewMode.TextSearch => "\u6587\u5b57\u641c\u7d22",
        _ => ""
    };

    private static AiPathInfo ParseDetail(string payload)
    {
        var colonIdx = payload.IndexOf(':');
        if (colonIdx < 0)
            return new AiPathInfo(true, AiViewMode.People); // fallback

        var tagType = payload[..colonIdx];
        var tagValue = payload[(colonIdx + 1)..];

        if (tagType == "face")
        {
            int.TryParse(tagValue, out var clusterId);
            return new AiPathInfo(false, AiViewMode.People, true, clusterId, null, null);
        }

        var mode = tagType switch
        {
            "scene" or "object" or "animal" => AiViewMode.Categories,
            "location" => AiViewMode.Locations,
            "date" => AiViewMode.Dates,
            _ => AiViewMode.Categories
        };

        return new AiPathInfo(false, mode, false, null, tagType, tagValue);
    }
}

public readonly record struct AiPathInfo(
    bool IsTopLevel,
    AiViewMode Mode,
    bool IsFaceDetail = false,
    int? FaceClusterId = null,
    string? TagType = null,
    string? TagValue = null);
