namespace MacExplorer.Services;

/// <summary>
/// Constants for virtual/sentinel path prefixes used throughout the app.
/// These represent non-filesystem paths for AI views, collections, archives, etc.
/// </summary>
public static class VirtualPath
{
    /// <summary>Prefix for AI view paths: __ai:face:123, __ai:categories, etc.</summary>
    public const string AiPrefix = "__ai:";

    /// <summary>Prefix for archive browsing paths: __archive:/path/to/file.zip#internal/path</summary>
    public const string ArchivePrefix = "__archive:";

    /// <summary>Prefix for collection paths: __collection:123</summary>
    public const string CollectionPrefix = "__collection:";

    /// <summary>System trash directory path (not a sentinel prefix but related).</summary>
    public const string SystemTrash = "__system_trash__";

    // AI sub-paths
    /// <summary>AI people/face view top-level path.</summary>
    public const string AiPeople = "__ai:people";

    /// <summary>AI categories view top-level path.</summary>
    public const string AiCategories = "__ai:categories";

    /// <summary>AI locations view top-level path.</summary>
    public const string AiLocations = "__ai:locations";

    /// <summary>AI dates view top-level path.</summary>
    public const string AiDates = "__ai:dates";

    /// <summary>AI text search top-level path.</summary>
    public const string AiTextSearch = "__ai:textsearch";

    /// <summary>Prefix for AI face cluster paths: __ai:face:123.</summary>
    public const string AiFacePrefix = "__ai:face:";

    /// <summary>Prefix for AI scene paths: __ai:scene:xxx.</summary>
    public const string AiScenePrefix = "__ai:scene:";

    /// <summary>Prefix for AI object paths: __ai:object:xxx.</summary>
    public const string AiObjectPrefix = "__ai:object:";

    /// <summary>Prefix for AI animal paths: __ai:animal:xxx.</summary>
    public const string AiAnimalPrefix = "__ai:animal:";

    /// <summary>Prefix for AI location paths: __ai:location:xxx.</summary>
    public const string AiLocationPrefix = "__ai:location:";

    /// <summary>Prefix for AI date paths: __ai:date:xxx.</summary>
    public const string AiDatePrefix = "__ai:date:";
}