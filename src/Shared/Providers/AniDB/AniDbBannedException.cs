// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using System;

namespace AniMetaPlus.Providers.AniDB;

/// <summary>
/// Thrown when AniDB has temporarily banned this client, so no further request may be
/// sent until the ban has been given time to lapse.
/// </summary>
public class AniDbBannedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbBannedException"/> class.
    /// </summary>
    public AniDbBannedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbBannedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public AniDbBannedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbBannedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public AniDbBannedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the time that must pass before the plugin will contact AniDB again.
    /// </summary>
    public TimeSpan RetryAfter { get; init; }
}

/// <summary>
/// Thrown instead of asking AniDB when it cannot be asked at all: no client name is set, or
/// AniDB has turned away the one that is. Derived from <see cref="AniDbBannedException"/> so that
/// everything which already gives way to a ban gives way to this too.
/// </summary>
public class AniDbUnavailableException : AniDbBannedException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbUnavailableException"/> class.
    /// </summary>
    public AniDbUnavailableException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbUnavailableException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public AniDbUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbUnavailableException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public AniDbUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets a value indicating whether AniDB rejected the client name, rather than none being set.
    /// </summary>
    public bool ClientRejected { get; init; }
}

/// <summary>
/// AniDB answered a request for one anime with an error document: an id it does not know, or
/// another refusal of that request alone.
/// </summary>
public class AniDbRefusedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbRefusedException"/> class.
    /// </summary>
    /// <param name="message">What AniDB said.</param>
    public AniDbRefusedException(string message)
        : base(message)
    {
    }
}
