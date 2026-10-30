using System.Diagnostics.CodeAnalysis;

namespace UrlShortener.Api.Logging;

[ExcludeFromCodeCoverage]
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, path, _gate);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category, string path, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Exception text is deliberately not written: driver messages can contain hosts and ports.
            var line = $"{DateTime.UtcNow:O} [{logLevel}] {category}: {formatter(state, exception)}{Environment.NewLine}";
            try
            {
                lock (gate)
                {
                    File.AppendAllText(path, line);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
