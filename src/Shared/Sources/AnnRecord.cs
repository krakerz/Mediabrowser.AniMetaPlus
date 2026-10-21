using System;
using System.Collections.Generic;

namespace AniMetaPlus.Sources;

/// <summary>
/// One person on an Anime News Network entry's staff.
/// </summary>
public sealed class AnnStaff
{
    /// <summary>Gets or sets the task, as ANN words it, such as "Director".</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>Gets or sets the person's name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// One voice actor on an Anime News Network entry's cast, for one language.
/// </summary>
public sealed class AnnCast
{
    /// <summary>Gets or sets the language code the role is voiced in, such as "EN" or "JA".</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the character.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets the actor's name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// One company credited on an Anime News Network entry.
/// </summary>
public sealed class AnnCompany
{
    /// <summary>Gets or sets the task, as ANN words it, such as "Animation Production".</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>Gets or sets the company's name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// One episode of an Anime News Network entry.
/// </summary>
public sealed class AnnEpisode
{
    /// <summary>Gets or sets the number as ANN writes it: "1", but also "1.5" or "S1".</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the English title, or <c>null</c> where ANN has none.</summary>
    public string? EnglishTitle { get; set; }
}

/// <summary>
/// What Anime News Network's encyclopedia says about one anime, kept as it is cached: only the
/// parts this plugin reads. Every part is optional there, so every part may be empty here.
/// </summary>
public sealed class AnnRecord
{
    /// <summary>Gets or sets the ANN id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether ANN knows the id. An unknown id is cached too, as
    /// not found, so that a wrong link is not asked about on every refresh.
    /// </summary>
    public bool Found { get; set; }

    /// <summary>Gets or sets the main title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the bayesian score, from 0 to 10.</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets how many votes the score is from.</summary>
    public int? Votes { get; set; }

    /// <summary>Gets or sets the staff.</summary>
    public List<AnnStaff> Staff { get; set; } = [];

    /// <summary>Gets or sets the cast, in every language ANN lists.</summary>
    public List<AnnCast> Cast { get; set; } = [];

    /// <summary>Gets or sets the credited companies.</summary>
    public List<AnnCompany> Companies { get; set; } = [];

    /// <summary>Gets or sets the episodes.</summary>
    public List<AnnEpisode> Episodes { get; set; } = [];

    /// <summary>Gets or sets when it was fetched.</summary>
    public DateTime FetchedUtc { get; set; }
}
