using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using SeatReservation.Api.Options;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.Api.Admission;

/// <summary>
/// Builds the single <see cref="ConcurrencyLimiter"/> behind policy <c>db</c> and keeps a handle on it for
/// <c>reservation_queue_length</c>. The rate-limiting middleware disposes a partition's limiter after ~10 s idle and asks
/// for a new one on the next request, so this hands out a fresh limiter per call and remembers the latest.
/// </summary>
public sealed class DbAdmissionLimiter(IOptions<AdmissionOptions> admission, IOptions<DatabaseOptions> database)
{
    private volatile ConcurrencyLimiter? _current;

    /// <summary><c>Admission:PermitLimit</c>, or the main pool size when unset.</summary>
    public int PermitLimit { get; } = admission.Value.PermitLimit ?? database.Value.MaxPoolSize;

    public int QueueLimit { get; } = admission.Value.QueueLimit;

    /// <summary>Requests waiting for a permit right now; 0 before the first DB-bound request and while idle.</summary>
    public long QueuedCount
    {
        get
        {
            try
            {
                return _current?.GetStatistics()?.CurrentQueuedCount ?? 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;   // disposed because idle: nothing queued
            }
        }
    }

    internal ConcurrencyLimiter Create()
    {
        var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = PermitLimit,
            QueueLimit = QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
        _current = limiter;
        return limiter;
    }
}
