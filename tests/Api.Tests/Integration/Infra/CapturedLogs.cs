using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Every line the app's logging lets through, as the configured filters shape it: added
/// beside the real providers with <see cref="ApiFactory.WithServices"/>, so a category the
/// app's settings hold at Warning never reaches it at Information.
/// </summary>
public sealed class CapturedLogs : ILoggerProvider
{
    public sealed record Line(string Category, LogLevel Level, string Message);

    private readonly ConcurrentQueue<Line> _lines = new();

    public IReadOnlyList<Line> Lines => _lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<Line> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue(new Line(category, logLevel,
                formatter(state, exception) + (exception is null ? "" : " | " + exception)));
        }
    }
}
