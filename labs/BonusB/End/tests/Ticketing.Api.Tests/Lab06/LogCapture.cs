using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Ticketing.Api.Tests.Lab06;

/// <summary>Collects log entries, with their structured properties and scopes, for assertions.</summary>
public sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    public record Entry(
        string Category, LogLevel Level, EventId EventId, string Message,
        Dictionary<string, object?> Properties, Dictionary<string, object?> Scopes);

    private IExternalScopeProvider? _scopes;
    public ConcurrentQueue<Entry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    private sealed class Logger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : [];

            var scopes = new Dictionary<string, object?>();
            owner._scopes?.ForEachScope((scope, dict) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> items)
                    foreach (var (key, value) in items) dict[key] = value;
            }, scopes);

            owner.Entries.Enqueue(new Entry(category, logLevel, eventId, formatter(state, exception), properties, scopes));
        }
    }
}
