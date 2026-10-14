#if EMBY
using System;
using Microsoft.Extensions.Logging;
using EmbyLog = MediaBrowser.Model.Logging.ILogger;

namespace AniMetaPlus.Logging;

/// <summary>
/// Writes the shared code's structured log calls to Emby's logger, which takes finished text.
/// </summary>
internal sealed class EmbyLogger(EmbyLog inner, string category) : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = category + ": " + formatter(state, exception);

        switch (logLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                inner.Debug(message);
                break;
            case LogLevel.Information:
                inner.Info(message);
                break;
            case LogLevel.Warning:
                if (exception is null)
                {
                    inner.Warn(message);
                }
                else
                {
                    inner.Warn(message + " " + exception);
                }

                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                if (exception is null)
                {
                    inner.Error(message);
                }
                else
                {
                    inner.ErrorException(message, exception);
                }

                break;
        }
    }

    /// <summary>
    /// Creates a logger for a category, for code that is not built by the container.
    /// </summary>
    /// <param name="logManager">The server's log manager.</param>
    /// <param name="category">The category name.</param>
    /// <returns>The logger.</returns>
    public static ILogger Create(MediaBrowser.Model.Logging.ILogManager logManager, string category)
        => new EmbyLogger(logManager.GetLogger("AniMeta+"), category);
}
#endif
