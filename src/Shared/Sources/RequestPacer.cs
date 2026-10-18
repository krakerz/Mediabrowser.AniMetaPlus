using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AniMetaPlus.Sources;

/// <summary>
/// The limits one source's requests are held to: an hourly and a daily cap over rolling
/// windows, and bursts of a set size each followed by a rest.
/// </summary>
/// <param name="PerHour">Requests allowed in any hour; zero means no cap.</param>
/// <param name="PerDay">Requests allowed in any 24 hours; zero means no cap.</param>
/// <param name="BurstSize">Requests in one burst before a rest; zero means no bursts.</param>
/// <param name="Rest">How long a rest lasts, from the last request of the burst.</param>
public readonly record struct RequestLimits(int PerHour, int PerDay, int BurstSize, TimeSpan Rest);

/// <summary>
/// Counts a source's requests and says when the next may go out. Times are passed in, so the
/// rules can be checked without a clock. Not thread-safe: the caller locks.
/// </summary>
public sealed class RequestPacer
{
    private readonly Queue<DateTime> _sent = new();

    /// <summary>
    /// Gets how many requests the current burst has sent.
    /// </summary>
    public int BurstSent { get; private set; }

    /// <summary>
    /// Gets when the current rest ends, or <c>null</c> when not resting.
    /// </summary>
    public DateTime? RestUntilUtc { get; private set; }

    /// <summary>
    /// Gets when the last request went out.
    /// </summary>
    public DateTime? LastSentUtc { get; private set; }

    /// <summary>
    /// Records a request sent now.
    /// </summary>
    /// <param name="now">The time, in UTC.</param>
    /// <param name="limits">The limits in force.</param>
    public void Record(DateTime now, RequestLimits limits)
    {
        EndRestIfOver(now, limits);

        _sent.Enqueue(now);
        Trim(now);

        if (limits.BurstSize > 0)
        {
            BurstSent++;
            if (BurstSent >= limits.BurstSize)
            {
                RestUntilUtc = now + limits.Rest;
            }
        }

        LastSentUtc = now;
    }

    /// <summary>
    /// Says why a request may not go out now, and for how long.
    /// </summary>
    /// <param name="now">The time, in UTC.</param>
    /// <param name="limits">The limits in force.</param>
    /// <returns>The reason and the wait, or <c>null</c> when it may go out.</returns>
    public (string Reason, TimeSpan Wait)? Refusal(DateTime now, RequestLimits limits)
    {
        EndRestIfOver(now, limits);

        if (RestUntilUtc is { } until && until > now)
        {
            return (string.Format(CultureInfo.InvariantCulture, "resting after a burst of {0} until {1:u}", BurstSent, until), until - now);
        }

        Trim(now);
        var (hour, day) = Totals(now);

        if (limits.PerHour > 0 && hour >= limits.PerHour)
        {
            // The hour frees up as its oldest request leaves the window.
            var oldest = _sent.Where(t => t > now.AddHours(-1)).Min();
            return (string.Format(CultureInfo.InvariantCulture, "the hourly cap of {0} is used", limits.PerHour), oldest.AddHours(1) - now);
        }

        if (limits.PerDay > 0 && day >= limits.PerDay)
        {
            return (string.Format(CultureInfo.InvariantCulture, "the daily cap of {0} is used", limits.PerDay), _sent.Peek().AddDays(1) - now);
        }

        return null;
    }

    /// <summary>
    /// Counts the requests of the last hour and the last day.
    /// </summary>
    /// <param name="now">The time, in UTC.</param>
    /// <returns>The counts.</returns>
    public (int LastHour, int LastDay) Totals(DateTime now)
    {
        Trim(now);
        var hourAgo = now.AddHours(-1);

        return (_sent.Count(t => t > hourAgo), _sent.Count);
    }

    /// <summary>
    /// Writes the state as text, for keeping across a restart.
    /// </summary>
    /// <returns>The state.</returns>
    public string Save()
        => string.Join(
            ';',
            BurstSent.ToString(CultureInfo.InvariantCulture),
            (RestUntilUtc?.Ticks ?? 0).ToString(CultureInfo.InvariantCulture),
            (LastSentUtc?.Ticks ?? 0).ToString(CultureInfo.InvariantCulture),
            string.Join(',', _sent.Select(t => t.Ticks.ToString(CultureInfo.InvariantCulture))));

    /// <summary>
    /// Takes back a state written by <see cref="Save"/>, or by earlier versions, which kept only
    /// the request times.
    /// </summary>
    /// <param name="text">The state.</param>
    /// <param name="now">The time, in UTC.</param>
    public void Load(string? text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var parts = text.Split(';');
        var times = parts.Length >= 4 ? parts[3] : parts[0];

        if (parts.Length >= 4)
        {
            BurstSent = Math.Max(BurstSent, ParseInt(parts[0]));
            RestUntilUtc = Later(RestUntilUtc, ParseTime(parts[1]));
            LastSentUtc = Later(LastSentUtc, ParseTime(parts[2]));
        }

        var merged = _sent.Concat(times.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ParseTime).OfType<DateTime>())
            .Distinct()
            .OrderBy(t => t)
            .ToList();
        _sent.Clear();
        foreach (var at in merged)
        {
            _sent.Enqueue(at);
        }

        LastSentUtc = Later(LastSentUtc, merged.Count > 0 ? merged[^1] : null);
        Trim(now);
    }

    // A rest that has run out starts a fresh burst, and so does a quiet spell as long as a rest.
    private void EndRestIfOver(DateTime now, RequestLimits limits)
    {
        if (RestUntilUtc is { } until && until <= now)
        {
            RestUntilUtc = null;
            BurstSent = 0;
        }
        else if (RestUntilUtc == null && BurstSent > 0 && LastSentUtc is { } last && limits.Rest > TimeSpan.Zero && now - last >= limits.Rest)
        {
            BurstSent = 0;
        }
    }

    private void Trim(DateTime now)
    {
        var dayAgo = now.AddDays(-1);

        while (_sent.Count > 0 && _sent.Peek() < dayAgo)
        {
            _sent.Dequeue();
        }
    }

    private static int ParseInt(string text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static DateTime? ParseTime(string text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    private static DateTime? Later(DateTime? a, DateTime? b)
        => a == null ? b : b == null ? a : (a > b ? a : b);
}
