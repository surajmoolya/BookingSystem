using System.IO;
using System.Net.Sockets;
using Npgsql;
using SeatReservation.Application.Exceptions;

namespace SeatReservation.Infrastructure.Transactions;

public enum DbErrorKind
{
    /// <summary>Worth re-running the whole transaction: deadlock, serialization failure, lock timeout, query cancelled.</summary>
    Transient,

    /// <summary>The database can't be reached or is shutting down; retry, then answer 503.</summary>
    Unavailable,

    /// <summary>A concurrent request won the (user, idempotency key) race. Not an error: the caller re-reads and replays.</summary>
    IdempotencyRace,

    /// <summary>The caller (or host shutdown) cancelled the operation.</summary>
    Cancelled,

    /// <summary>Everything else: a bug. Never retried, surfaces as 500.</summary>
    Bug,
}

/// <summary>Maps database exceptions onto the retry matrix of lld §7 (D-055).</summary>
public static class PgErrorClassifier
{
    public const string IdempotencyConstraint = "uq_reservations_user_key";

    public static DbErrorKind Classify(Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return DbErrorKind.Cancelled;

            case DuplicateIdempotencyKeyException:
                return DbErrorKind.IdempotencyRace;

            case PostgresException pg:
                return ClassifySqlState(pg);

            case NpgsqlException { IsTransient: true }:
                return DbErrorKind.Unavailable;
        }

        return HasNetworkOrTimeoutCause(exception) ? DbErrorKind.Unavailable : DbErrorKind.Bug;
    }

    /// <summary>True for the one transient class that is retried only once: a command timeout or cancelled query.</summary>
    public static bool IsQueryCanceled(Exception exception) => exception is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled };

    private static DbErrorKind ClassifySqlState(PostgresException pg) => pg.SqlState switch
    {
        PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.LockNotAvailable
            or PostgresErrorCodes.QueryCanceled => DbErrorKind.Transient,

        PostgresErrorCodes.AdminShutdown
            or PostgresErrorCodes.CrashShutdown
            or PostgresErrorCodes.CannotConnectNow
            or PostgresErrorCodes.TooManyConnections => DbErrorKind.Unavailable,

        PostgresErrorCodes.UniqueViolation when pg.ConstraintName == IdempotencyConstraint => DbErrorKind.IdempotencyRace,

        var state when state.StartsWith("08", StringComparison.Ordinal) => DbErrorKind.Unavailable,   // connection exception class

        _ => DbErrorKind.Bug,
    };

    // Npgsql 8 reports pool exhaustion and failed connects as an NpgsqlException wrapping a TimeoutException / SocketException.
    private static bool HasNetworkOrTimeoutCause(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SocketException or IOException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
