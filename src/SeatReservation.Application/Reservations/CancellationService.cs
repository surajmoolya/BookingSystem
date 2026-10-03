using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Logging;
using SeatReservation.Application.Options;

namespace SeatReservation.Application.Reservations;

/// <summary>
/// Owner-only cancellation (lld §6.4, D-011). The lock order is user lock → reservation row → seat rows; reserve takes
/// user lock → seat rows and only inserts new reservation rows, so the two can't form a cycle (lld §6.5).
/// </summary>
public sealed class CancellationService(
    ITransactionRunner tx,
    IClock clock,
    IReadinessState readiness,
    IReservationMetrics metrics,
    IOptions<ReservationOptions> options,
    ILogger<CancellationService> logger)
{
    /// <summary>Decides, then records the outcome once, after the transaction has finished (a retried delegate never counts twice).</summary>
    public async Task<CancelOutcome> CancelAsync(CancelReservationCommand command, CancellationToken ct)
    {
        if (!readiness.IsReady)
        {
            throw new NotReadyException();
        }

        var outcome = await tx.RunAsync("cancel", (uow, c) => CancelLockedAsync(uow, command, c), ct);

        if (outcome is CancelOutcome.Cancelled cancelled)
        {
            var r = cancelled.Reservation;
            metrics.Cancelled();
            logger.ReservationCancelled(r.Id, r.UserId, r.ShowId, r.Seats);
        }

        return outcome;
    }

    // Runs inside the transaction and may be re-run whole on a transient error, so it touches only the unit of work.
    private async Task<TxResult<CancelOutcome>> CancelLockedAsync(IUnitOfWork uow, CancelReservationCommand command, CancellationToken ct)
    {
        await uow.SetLockTimeoutAsync(options.Value.LockTimeout, ct);

        // The caller's lock, taken first like every reserve: the owner's reserves and cancels run one at a time, so a
        // cancel can't free seats while the same user's reserve is counting them against the limit.
        await uow.UserLock.AcquireAsync(command.UserId, ct);

        // Row lock: concurrent cancels of one reservation queue here, and only the first sees it confirmed.
        var reservation = await uow.Reservations.GetByIdForUpdateAsync(command.ReservationId, ct);
        if (reservation is null)
        {
            return TxResult<CancelOutcome>.RollbackWith(new CancelOutcome.NotFound());
        }

        if (reservation.UserId != command.UserId)
        {
            return TxResult<CancelOutcome>.RollbackWith(new CancelOutcome.NotOwner());
        }

        if (reservation.Status == ReservationStatus.Cancelled)
        {
            return TxResult<CancelOutcome>.RollbackWith(new CancelOutcome.AlreadyCancelled(reservation));
        }

        // Keyed by reservation id, never by label: a stale cancel can't free a seat someone else has since booked (D-040).
        await uow.Seats.ReleaseByReservationAsync(reservation.Id, ct);

        var at = Timestamps.TruncateToMicroseconds(clock.UtcNow);
        await uow.Reservations.MarkCancelledAsync(reservation.Id, at, ct);
        return TxResult<CancelOutcome>.CommitWith(new CancelOutcome.Cancelled(reservation.AsCancelled(at)));
    }
}
