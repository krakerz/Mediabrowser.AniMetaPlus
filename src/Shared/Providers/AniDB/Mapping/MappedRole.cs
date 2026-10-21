namespace AniMetaPlus.Providers.AniDB.Mapping;

/// <summary>
/// What the mappings say an AniDB entry is of its show: which season it fills, from which
/// episode of it, or that it is a movie.
/// </summary>
/// <param name="Season">The season the entry fills, 0 for the specials, or <c>null</c> where the mappings name none.</param>
/// <param name="EpisodeOffset">How many episodes of that season come before the entry's first.</param>
/// <param name="Absolute">Whether the entry is numbered straight through the whole show.</param>
/// <param name="Movie">Whether the mappings identify the entry as a movie.</param>
internal sealed record MappedRole(int? Season, int EpisodeOffset, bool Absolute, bool Movie);
