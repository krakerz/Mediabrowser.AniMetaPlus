using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
            lane.ThrowIfCapped(source, setting.MaxPerHour, setting.MaxPerDay);

            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), lane.NextTimestamp);
            while (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), lane.NextTimestamp);
            }

            lane.NextTimestamp = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (Math.Max(setting.RequestIntervalMs, 0) / 1000d));
            lane.Count();
        }
        finally
        {
            lane.Gate.Release();
        }

        var started = Stopwatch.GetTimestamp();

        // A source that does not answer holds up the item's whole refresh, so it gets a bounded
        // wait rather than the client's default of 100 seconds.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            var result = await request(timeout.Token).ConfigureAwait(false);

            BlockStore.RecordSuccess(source);
            Log(source, target, itemKey, "ok", null, started);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (ex is SourceBlockedException)
            {
                throw;
            }

            // A provider that does not do this at all is not failing; nothing is remembered.
            if (ex is NotImplementedException or NotSupportedException)
            {
                Log(source, target, itemKey, "unsupported", null, started);

                throw new SourceBlockedException(source + " does not support this.", ex) { SourceId = source };
            }

            var failure = BlockStore.Classify(ex);
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
        private readonly Queue<DateTime> _sent = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long NextTimestamp { get; set; } = Stopwatch.GetTimestamp();

        public void Count()
        {
            lock (_sent)
            {
                _sent.Enqueue(DateTime.UtcNow);
                Trim();
            }
        }

        public (int LastHour, int LastDay) Totals()
        {
            lock (_sent)
            {
                Trim();
                var hourAgo = DateTime.UtcNow.AddHours(-1);
                var hour = 0;

                foreach (var at in _sent)
                {
                    if (at > hourAgo)
                    {
                        hour++;
                    }
                }

                return (hour, _sent.Count);
            }
        }

        public void ThrowIfCapped(string source, int perHour, int perDay)
        {
            if (perHour <= 0 && perDay <= 0)
            {
                return;
            }

            var (hour, day) = Totals();

            if ((perHour > 0 && hour >= perHour) || (perDay > 0 && day >= perDay))
            {
                throw new SourceBlockedException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} has used its request cap ({1} this hour, {2} today).",
                    source,
                    hour,
                    day))
                {
                    SourceId = source
                };
            }
        }

        private void Trim()
        {
            var dayAgo = DateTime.UtcNow.AddDays(-1);

            while (_sent.Count > 0 && _sent.Peek() < dayAgo)
            {
                _sent.Dequeue();
            }
        }
    }
}
