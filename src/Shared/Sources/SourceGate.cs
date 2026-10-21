using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Data;

namespace AniMetaPlus.Sources;

/// <summary>
/// Sends every request to a source one at a time, spaced by the source's interval and within
/// its hourly and daily caps, and remembers how each one went. A request the blocks say cannot
/// succeed is not sent at all.
/// </summary>
public static class SourceGate
{
    private static readonly ConcurrentDictionary<string, Lane> _lanes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long one request may take before the source counts as not answering.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long one call to a server's own TVDB or TMDB provider may take. One call is several
    /// requests of that provider's (series, episodes, people), so it gets longer than one request.
    /// </summary>
    public static readonly TimeSpan DelegatedRequestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Sends a request through the source's gate.
    /// </summary>
    /// <typeparam name="T">What the request returns.</typeparam>
    /// <param name="source">The source id.</param>
    /// <param name="itemKey">The item key, or empty.</param>
    /// <param name="itemName">The item's name, for the settings page.</param>
    /// <param name="kind">The kind of data, or empty.</param>
    /// <param name="target">What is asked for, without keys, for the request log.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the request returned.</returns>
    /// <exception cref="SourceBlockedException">The source may not be asked, or the request failed and is now blocked.</exception>
    public static async Task<T> Run<T>(
        string source,
        string itemKey,
        string itemName,
        string kind,
        string target,
        Func<CancellationToken, Task<T>> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ThrowIfBlocked(source, itemKey, kind);

        var setting = Plugin.Instance.Configuration.GetSource(source);
        var lane = _lanes.GetOrAdd(source, _ => new Lane());

        await lane.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Asked again: a request ahead in the queue may have been blocked meanwhile.
            ThrowIfBlocked(source, itemKey, kind);
            var limits = SourceDefaults.Limits(setting);
            lane.ThrowIfCapped(source, limits);

            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), lane.NextTimestamp);
            while (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), lane.NextTimestamp);
            }

            lane.NextTimestamp = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (Math.Max(setting.RequestIntervalMs, 0) / 1000d));
            lane.Count(limits);
        }
        finally
        {
            lane.Gate.Release();
        }

        var started = Stopwatch.GetTimestamp();
        var startedUtc = DateTime.UtcNow;

        // A source that does not answer holds up the item's whole refresh, so it gets a bounded
        // wait rather than the client's default of 100 seconds.
        var delegated = SourceIds.IsDelegated(source);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(delegated ? DelegatedRequestTimeout : RequestTimeout);

        try
        {
            var result = await request(timeout.Token).ConfigureAwait(false);

            BlockStore.RecordSuccess(source, startedUtc);
            Log(source, target, itemKey, "ok", null, started);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The server shutting down or restarting under a request: nothing the source did, and
            // nothing to remember against it. Blocking on it once kept items away from a source
            // for days after a mere restart.
            if (ex is SourceBlockedException || BlockStore.IsShutdown(ex))
            {
                throw;
            }

            // A provider that does not do this at all is not failing; nothing is remembered.
            if (ex is NotImplementedException or NotSupportedException)
            {
                Log(source, target, itemKey, "unsupported", null, started);

                throw new SourceBlockedException(source + " does not support this.", ex) { SourceId = source };
            }

            var failure = TimeoutScope(BlockStore.Classify(ex), ex, delegated);
            var block = BlockStore.Record(source, failure, itemKey, itemName, kind);
            Log(source, target, itemKey, failure.Reason.ToString(), failure.HttpStatus, started);

            throw new SourceBlockedException(source + ": " + failure.Message, ex)
            {
                SourceId = source,
                Block = block
            };
        }
    }

    /// <summary>
    /// Narrows a delegated provider's timeout to the item. Its slowness is usually that item's
    /// size (a long show's episode list), and a source-wide block would stop every other show.
    /// </summary>
    /// <param name="failure">The failure as classified.</param>
    /// <param name="exception">What was thrown.</param>
    /// <param name="delegated">Whether the source is a server's own provider.</param>
    /// <returns>The failure to record.</returns>
    internal static SourceFailure TimeoutScope(SourceFailure failure, Exception exception, bool delegated)
        => delegated && failure.Scope == BlockScope.Source && exception is OperationCanceledException or TimeoutException
            ? failure with { Scope = BlockScope.Item }
            : failure;

    /// <summary>
    /// Records a request that was answered with nothing for the item, so it is not asked again
    /// for a while.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="itemKey">The item key.</param>
    /// <param name="itemName">The item's name.</param>
    /// <param name="kind">The kind of data, or empty.</param>
    /// <param name="message">What was missing.</param>
    public static void RecordEmpty(string source, string itemKey, string itemName, string kind, string message)
        => BlockStore.Record(source, new SourceFailure(BlockScope.Item, BlockReason.Empty, message), itemKey, itemName, kind);

    /// <summary>
    /// Gets how many requests went to each source in the last hour and day, for the settings page.
    /// </summary>
    /// <returns>The counts per source.</returns>
    public static IReadOnlyDictionary<string, (int LastHour, int LastDay)> Counts()
    {
        var counts = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (source, lane) in _lanes)
        {
            counts[source] = lane.Totals();
        }

        return counts;
    }

    private static void ThrowIfBlocked(string source, string itemKey, string kind)
    {
        var setting = Plugin.Instance.Configuration.GetSource(source);
        if (!setting.Enabled)
        {
            throw new SourceBlockedException(source + " is turned off.") { SourceId = source };
        }

        var block = BlockStore.SourceBlock(source) ?? BlockStore.ItemBlock(source, itemKey, kind);
        if (block != null)
        {
            throw new SourceBlockedException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is blocked ({1}){2}.",
                source,
                block.Reason,
                block.ExpiresUtc is { } until ? " until " + until.ToString("u", CultureInfo.InvariantCulture) : string.Empty))
            {
                SourceId = source,
                Block = block
            };
        }
    }

    /// <summary>
    /// Counts a request sent through a source's own queue rather than through this gate - AniDB
    /// paces its API itself, with its ban handling - so that it shows in the counts and its caps
    /// apply. Throws when the source's hourly or daily cap is used up.
    /// </summary>
    /// <param name="source">The source id.</param>
    public static void CountOwnRequest(string source)
    {
        var setting = Plugin.Instance.Configuration.GetSource(source);
        var lane = _lanes.GetOrAdd(source, _ => new Lane());

        lane.CountUnlessCapped(source, SourceDefaults.Limits(setting));

        if (string.Equals(source, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
        {
            SaveAniDbCounts(lane);
        }
    }

    /// <summary>
    /// Whether a source's hourly or daily cap is used up, so that work bound for it can wait
    /// rather than be turned away.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <returns><c>true</c> when the next request would be refused.</returns>
    public static bool IsCapped(string source)
        => _lanes.GetOrAdd(source, _ => new Lane()).Refusal(SourceDefaults.Limits(Plugin.Instance.Configuration.GetSource(source))) != null;

    /// <summary>
    /// How many more requests a source's limits let through right now: what is left of the
    /// current burst, the hour and the day, whichever is least. A scan or queue run sends no
    /// more than this, so it never queues refreshes that would only be turned away.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <returns>The number left; <see cref="int.MaxValue"/> where the source has no limits.</returns>
    public static int RequestsLeft(string source)
    {
        var limits = SourceDefaults.Limits(Plugin.Instance.Configuration.GetSource(source));
        var lane = _lanes.GetOrAdd(source, _ => new Lane());

        if (lane.Refusal(limits) != null)
        {
            return 0;
        }

        var (hour, day) = lane.Totals();
        var (burstSent, _) = lane.BurstState(limits);
        var left = int.MaxValue;

        if (limits.BurstSize > 0)
        {
            left = Math.Min(left, limits.BurstSize - burstSent);
        }

        if (limits.PerHour > 0)
        {
            left = Math.Min(left, limits.PerHour - hour);
        }

        if (limits.PerDay > 0)
        {
            left = Math.Min(left, limits.PerDay - day);
        }

        return Math.Max(left, 0);
    }

    /// <summary>
    /// How a source stands against its bursts: how many the current burst has sent, and when
    /// its rest ends, if it is resting.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <returns>The burst state.</returns>
    public static (int BurstSent, DateTime? RestUntilUtc) BurstState(string source)
        => _lanes.GetOrAdd(source, _ => new Lane()).BurstState(SourceDefaults.Limits(Plugin.Instance.Configuration.GetSource(source)));

    // AniDB's counts outlive a restart: its caps are what keep a ban away, and a restart in the
    // middle of a library refresh would otherwise hand it a fresh allowance.
    private const string AniDbCountsState = "anidb-request-times";

    /// <summary>
    /// Puts back AniDB's request times from before the restart. Run at startup.
    /// </summary>
    public static void RestoreAniDbCounts()
    {
        try
        {
            _lanes.GetOrAdd(SourceIds.AniDb, _ => new Lane()).Load(AniMetaDb.Current.GetState(AniDbCountsState));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Counting starts afresh; the hourly cap still applies from here on.
        }
    }

    private static void SaveAniDbCounts(Lane lane)
    {
        try
        {
            AniMetaDb.Current.SetState(AniDbCountsState, lane.Save());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The count in memory still holds until the next restart.
        }
    }

    /// <summary>
    /// Writes a request sent through a source's own queue to the request log.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="target">What was asked for.</param>
    /// <param name="itemKey">The item it was asked for.</param>
    /// <param name="outcome">How it went.</param>
    /// <param name="status">The HTTP status, when there was one.</param>
    /// <param name="started">When it was sent, as a <see cref="Stopwatch"/> timestamp.</param>
    public static void LogOwnRequest(string source, string target, string itemKey, string outcome, int? status, long started)
        => Log(source, target, itemKey, outcome, status, started);

    private static void Log(string source, string target, string itemKey, string outcome, int? status, long started)
    {
        try
        {
            AniMetaDb.Current.LogRequest(new RequestLogRecord
            {
                Source = source,
                Target = target,
                ItemKey = itemKey,
                Outcome = outcome,
                HttpStatus = status,
                DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                AtUtc = DateTime.UtcNow
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The log is a convenience; losing an entry must not fail the request.
        }
    }

    /// <summary>
    /// The queue, pacing and counts of one source.
    /// </summary>
    private sealed class Lane
    {
        private readonly RequestPacer _pacer = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long NextTimestamp { get; set; } = Stopwatch.GetTimestamp();

        public void Count(RequestLimits limits)
        {
            lock (_pacer)
            {
                _pacer.Record(DateTime.UtcNow, limits);
            }
        }

        public void Load(string? state)
        {
            lock (_pacer)
            {
                _pacer.Load(state, DateTime.UtcNow);
            }
        }

        public string Save()
        {
            lock (_pacer)
            {
                return _pacer.Save();
            }
        }

        public (int LastHour, int LastDay) Totals()
        {
            lock (_pacer)
            {
                return _pacer.Totals(DateTime.UtcNow);
            }
        }

        public (string Reason, TimeSpan Wait)? Refusal(RequestLimits limits)
        {
            lock (_pacer)
            {
                return _pacer.Refusal(DateTime.UtcNow, limits);
            }
        }

        public (int BurstSent, DateTime? RestUntilUtc) BurstState(RequestLimits limits)
        {
            lock (_pacer)
            {
                // Asking refreshes a rest that has run out.
                _pacer.Refusal(DateTime.UtcNow, limits);
                return (_pacer.BurstSent, _pacer.RestUntilUtc);
            }
        }

        public void ThrowIfCapped(string source, RequestLimits limits)
        {
            if (Refusal(limits) is { } refusal)
            {
                throw Held(source, refusal);
            }
        }

        // Checked and counted under one lock: two requests checking together could both pass and
        // go one over the cap.
        public void CountUnlessCapped(string source, RequestLimits limits)
        {
            lock (_pacer)
            {
                var now = DateTime.UtcNow;

                if (_pacer.Refusal(now, limits) is { } refusal)
                {
                    throw Held(source, refusal);
                }

                _pacer.Record(now, limits);
            }
        }

        private static SourceBlockedException Held(string source, (string Reason, TimeSpan Wait) refusal)
            => new(source + " is held back: " + refusal.Reason + ".")
            {
                SourceId = source,
                RetryAfter = refusal.Wait
            };
    }
}
