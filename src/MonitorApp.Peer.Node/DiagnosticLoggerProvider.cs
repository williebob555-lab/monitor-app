using Microsoft.Extensions.Logging;
using MonitorApp.Peer.Application;

namespace MonitorApp.Peer.Node;

/// <summary>Copies the node's own log messages (updates, start and stop) and every warning into <see cref="DiagnosticLog"/>.</summary>
public sealed class DiagnosticLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(string category) : ILogger
    {
        private readonly string area = category.Contains("Update", StringComparison.Ordinal) ? "update" : "node";
        private readonly bool ours = category.StartsWith("MonitorApp.", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= (ours ? LogLevel.Information : LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = (logLevel >= LogLevel.Warning ? logLevel + ": " : string.Empty) + formatter(state, exception);
            if (exception is not null)
            {
                DiagnosticLog.Write(area, text, exception);
            }
            else
            {
                DiagnosticLog.Write(area, text);
            }
        }
    }
}
