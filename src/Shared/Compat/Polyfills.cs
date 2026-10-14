namespace AniMetaPlus;

#if !NET9_0_OR_GREATER
/// <summary>
/// Stands in for <c>System.Threading.Lock</c> on runtimes older than .NET 9, where a
/// <c>lock</c> on it is a plain monitor lock.
/// </summary>
internal sealed class Lock
{
}
#endif

#if EMBY
/// <summary>
/// The kinds of credit the shared code hands out. Jellyfin has an enum of this name; Emby's
/// <c>PersonType</c> knows fewer kinds, so each host converts at the point a person is created.
/// </summary>
internal enum PersonKind
{
    Unknown,
    Actor,
    Director,
    Composer,
    Writer,
    Artist,
    Producer,
    Conductor,
    Lyricist,
    GuestStar
}
#endif

#if EMBY
/// <summary>
/// The provider id keys the shared code names through Jellyfin's enum. Only the names are
/// used, and they are the same keys Emby files those ids under.
/// </summary>
internal enum MetadataProvider
{
    Imdb,
    Tmdb,
    Tvdb
}
#endif
