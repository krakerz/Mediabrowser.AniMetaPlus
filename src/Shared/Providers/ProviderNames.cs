namespace AniMetaPlus.Providers;

public static class ProviderNames
{
    /// <summary>
    /// The key AniDB ids are filed under on an item. Kept as the old AniDB plugins had it, so
    /// that items they identified keep their ids.
    /// </summary>
    public const string AniDb = "AniDB";

    /// <summary>
    /// The name the plugin's providers go by in the server's library settings.
    /// </summary>
    public const string Display = "AniMeta+";

    /// <summary>
    /// The name an image goes by in the server's image picker, which then shows where it came
    /// from. The servers download a picked image by its url, not by this name.
    /// </summary>
    /// <param name="source">The source id, one of <see cref="Sources.SourceIds"/>.</param>
    /// <returns>The name, such as "AniMeta+ (AniList)".</returns>
    public static string ForImage(string source) => Display + " (" + source + ")";
}
