using System;
using Microsoft.Extensions.Logging;
#if JELLYFIN
using Microsoft.Extensions.Logging.Abstractions;
#else
using ILogManager = MediaBrowser.Model.Logging.ILogManager;
#endif

namespace AniMetaPlus.Logging;

/// <summary>
/// The logger the shared providers are constructed with. A concrete type rather than
/// <see cref="ILogger{TCategoryName}"/>, because Emby's container cannot build the interface
/// but can build any class whose own dependencies it knows.
/// </summary>
/// <typeparam name="T">The category the logger writes under.</typeparam>
public sealed class PluginLog<T> : ILogger<T>
{
    private readonly ILogger _inner;

#if JELLYFIN
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLog{T}"/> class.
    /// </summary>
    /// <param name="factory">The server's logger factory.</param>
    public PluginLog(ILoggerFactory factory)
    {
        _inner = factory?.CreateLogger<T>() ?? (ILogger)NullLogger.Instance;
    }
#else
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLog{T}"/> class.
    /// </summary>
    /// <param name="logManager">The server's log manager.</param>
    public PluginLog(ILogManager logManager)
    {
        _inner = new EmbyLogger(logManager.GetLogger("AniMeta+"), typeof(T).Name);
    }
#endif

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
