using SeatReservation.Application.Abstractions;

namespace SeatReservation.Infrastructure.Migrations;

/// <summary>
/// Whether the schema is in place. Written by <see cref="MigrationRunner"/>, read by the logic layer
/// (<see cref="IReadinessState"/>) and, later, by the migrations health check.
/// </summary>
public sealed class MigrationState : IReadinessState
{
    private volatile bool _ready;
    private volatile bool _gaveUp;
    private volatile string? _lastError;
    private int _failedAttempts;

    public bool IsReady => _ready;

    /// <summary>True once the retry budget ran out; readiness stays red until the process restarts.</summary>
    public bool GaveUp => _gaveUp;

    public int FailedAttempts => Volatile.Read(ref _failedAttempts);

    /// <summary>Exception type and message of the latest failure. Never contains the connection string.</summary>
    public string? LastError => _lastError;

    public void MarkReady()
    {
        _lastError = null;
        _ready = true;
    }

    public void RecordFailure(Exception exception)
    {
        Interlocked.Increment(ref _failedAttempts);
        _lastError = $"{exception.GetType().Name}: {exception.Message}";
    }

    public void MarkGaveUp() => _gaveUp = true;
}
