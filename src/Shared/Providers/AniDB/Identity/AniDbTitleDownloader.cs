using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Titles;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMetaPlus.Providers.AniDB.Identity;

/// <summary>
/// Hands the title lookups the imported copy of the local titles dump. It never downloads:
/// the dump is refreshed by the scheduled task only, see <see cref="TitlesDump"/>.
/// </summary>
public class AniDbTitleDownloader : IAniDbTitleDownloader
{
    private static IApplicationPaths? _paths;
    private static ILogger _staticLogger = NullLogger.Instance;

    private readonly ILogger<AniDbTitleDownloader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbTitleDownloader"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{AniDbTitleDownloader}"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public AniDbTitleDownloader(ILogger<AniDbTitleDownloader> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _paths = applicationPaths;
        _staticLogger = logger;
    }

    /// <summary>
    /// Gets the path to the imported titles file, without requiring an instance.
    /// </summary>
    public static string StaticTitlesFilePath
        => _paths is null ? Path.Combine(Path.GetTempPath(), "animetaplus-titles.xml") : TitlesDump.ImportedPath(_paths);

    /// <inheritdoc />
    public string TitlesFilePath => StaticTitlesFilePath;

    /// <summary>
    /// Imports the local dump when the imported copy is missing or stale, without requiring an
    /// instance.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static Task LoadStatic(CancellationToken cancellationToken)
        => _paths is null ? Task.CompletedTask : TitlesDump.EnsureImported(_paths, _staticLogger, cancellationToken);

    /// <inheritdoc />
    public Task Load(CancellationToken cancellationToken)
        => _paths is null ? Task.CompletedTask : TitlesDump.EnsureImported(_paths, _logger, cancellationToken);
}
