#if JELLYFIN
using Jellyfin.Data.Enums;
#else
using MediaBrowser.Model.Entities;
#endif

namespace AniMetaPlus;

/// <summary>
/// Turns the kind of credit the shared code works with into the host's own.
/// </summary>
internal static class HostPeople
{
#if JELLYFIN
    public static PersonKind ToHost(PersonKind kind) => kind;
#else
    // Emby has no kind for an artist or an unclassified credit. Both are staff rather than
    // cast, and the role keeps AniDB's own wording, so they are listed as producers.
    public static PersonType ToHost(PersonKind kind) => kind switch
    {
        PersonKind.Actor => PersonType.Actor,
        PersonKind.Director => PersonType.Director,
        PersonKind.Composer => PersonType.Composer,
        PersonKind.Writer => PersonType.Writer,
        PersonKind.Conductor => PersonType.Conductor,
        PersonKind.Lyricist => PersonType.Lyricist,
        PersonKind.GuestStar => PersonType.GuestStar,
        _ => PersonType.Producer
    };
#endif
}
