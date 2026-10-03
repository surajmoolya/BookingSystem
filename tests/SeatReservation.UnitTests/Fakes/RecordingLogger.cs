using Microsoft.Extensions.Logging;

namespace SeatReservation.UnitTests.Fakes;

/// <summary>Captures log entries (level + rendered message) so tests can assert that an event such as <c>invariant.violation</c> was logged.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    /// <summary>Per entry, in the same order: the <see cref="EventId"/> name and the structured properties.</summary>
    public List<(string? EventName, IReadOnlyDictionary<string, object?> Properties)> Structured { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
        var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(p => p.Key, p => p.Value)
            : new Dictionary<string, object?>();
        Structured.Add((eventId.Name, properties));
    }
}
