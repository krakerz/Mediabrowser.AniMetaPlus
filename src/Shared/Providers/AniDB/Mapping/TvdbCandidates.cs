using System.Collections.Generic;

namespace AniMetaPlus.Providers.AniDB.Mapping;

/// <summary>
/// A TVDB show a MyAnimeList or AniList entry of the same anime is filed under.
/// </summary>
/// <param name="TvdbId">The TVDB show id.</param>
/// <param name="Link">The entry that names it, as the mappings key it: "mal:59415" or "anilist:179876".</param>
internal sealed record LinkedTvdb(string TvdbId, string Link);

/// <summary>
/// The TVDB show the AniDB-keyed mappings file a show under, and the other shows the entries
/// keyed by MyAnimeList and AniList file it under, where they differ.
/// </summary>
/// <param name="Primary">The AniDB-keyed id, or <c>null</c> where no source files the show under one.</param>
/// <param name="Overridden">Whether the owner's overrides gave it, which makes it final.</param>
/// <param name="Alternatives">The other ids, up to three, empty where any link agrees with the primary.</param>
internal sealed record TvdbCandidates(string? Primary, bool Overridden, IReadOnlyList<LinkedTvdb> Alternatives)
{
    /// <summary>
    /// Gets a value indicating whether the sources disagree about the show, and the owner has not settled it.
    /// </summary>
    public bool Disagree => Primary != null && !Overridden && Alternatives.Count > 0;
}
