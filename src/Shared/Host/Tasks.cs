using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Logging;
using AniMetaPlus.Titles;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Host;

/// <summary>
/// What the plugin's scheduled tasks share: the category, the trigger shape, and the server's
/// two ways of running a task.
/// </summary>
public abstract class AniMetaTask : IScheduledTask
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Key { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public string Category => "AniMeta+";

    /// <inheritdoc />
    public abstract IEnumerable<TaskTriggerInfo> GetDefaultTriggers();

#if EMBY
    /// <inheritdoc />
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) => Run(progress, cancellationToken);
#else
    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => Run(progress, cancellationToken);
#endif

    /// <summary>
    /// Runs the task.
    /// </summary>
    /// <param name="progress">Progress, from 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    protected abstract Task Run(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a trigger that runs every so often.
    /// </summary>
    /// <param name="interval">How often.</param>
    /// <returns>The trigger.</returns>
    protected static TaskTriggerInfo Every(TimeSpan interval) => new()
    {
#if EMBY
        Type = TaskTriggerInfo.TriggerInterval,
#else
        Type = TaskTriggerInfoType.IntervalTrigger,
#endif
        IntervalTicks = interval.Ticks
    };
}

/// <summary>
/// Downloads a fresh AniDB titles dump when one is due. The only thing that ever downloads it.
/// </summary>
public class TitlesRefreshTask(PluginLog<TitlesRefreshTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Refresh AniDB titles dump";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusTitlesRefresh";

    /// <inheritdoc />
    public override string Description => "Downloads a fresh AniDB titles dump when the one on disk is older than the interval set in AniMeta+, never more than once every 3 days.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromHours(12))];

    /// <inheritdoc />
    protected override async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = await TitlesDump.Refresh(Plugin.Instance.AppPaths, Plugin.Instance.GetHttpClient(), force: false, logger, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("AniMeta+ titles dump: {Message}", result.Message);

        // Imported either way, so that a dump put in place by hand is picked up.
        await TitlesDump.EnsureImported(Plugin.Instance.AppPaths, logger, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}

/// <summary>
/// Works through the queues: items waiting for a paused source, stand-in values to replace, and
/// items mapped by hand.
/// </summary>
public class QueueTask(PluginLog<QueueTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Process AniMeta+ queues";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusQueues";

    /// <inheritdoc />
    public override string Description => "Refreshes items waiting for a source that answers again, items with values a stand-in supplied, and items mapped by hand.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromMinutes(30))];

    /// <inheritdoc />
    protected override Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        => QueueProcessor.Run(logger, progress, cancellationToken);
}

/// <summary>
/// Identifies the anime libraries from local data first, and paces the refreshes that reach
/// AniDB.
/// </summary>
public class LibraryScanTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, PluginLog<LibraryScanTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Identify anime libraries";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusLibraryScan";

    /// <inheritdoc />
    public override string Description => "Identifies unidentified anime from the mapping sources and the local titles dump, lists what it cannot for mapping by hand, and refreshes a batch at a time, newest first.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromHours(1))];

    /// <inheritdoc />
    protected override Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var library = new HostLibrary(libraryManager, providerManager, fileSystem);
        HostServices.Refresher ??= library;

        return LibraryScan.Run(library.ListAnimeItems(), logger, progress, cancellationToken);
    }
}
