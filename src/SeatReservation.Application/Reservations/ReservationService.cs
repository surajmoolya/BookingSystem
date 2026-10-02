using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Options;
using SeatReservation.Application.Shows;

namespace SeatReservation.Application.Reservations;

/// <summary>
/// Every reserve decision (lld §6.2). There's no SQL here: the order of the port calls inside the transaction is the
/// correctness contract, and the unit tests pin it.
/// </summary>
public sealed class ReservationService(
    ShowCatalog catalog,
    ReserveSeatsValidator validator,
    IReservationReadRepository reservationReads,
    ITransactionRunner tx,
    IIdGenerator ids,
    IClock clock,
    IReadinessState readiness,
    IOptions<ReservationOptions> options)
{
    public async Task<ReservationOutcome> ReserveAsync(ReserveSeatsCommand command, CancellationToken ct)
    {
        if (!readiness.IsReady)
        {
            throw new NotReadyException();
        }

        var definition = await catalog.GetAsync(command.ShowId, ct);
        if (definition is null)
        {
            return new ReservationOutcome.ShowNotFound();
        }

        var invalid = validator.Validate(command, definition);
        if (invalid is not null)
        {
            return invalid;
        }

        // Over the limit whatever the user already holds: no need to ask the database.
        var limit = definition.Show.PerUserLimit;
        if (command.Seats.Count > limit)
        {
            return new ReservationOutcome.PerUserLimit(limit, Held: 0, Requested: command.Seats.Count);
        }

        // Ordinal sort: every transaction takes seat row locks in the same order, so two of them can't deadlock (D-030).
        var sorted = command.Seats.Select(s => s!).Order(StringComparer.Ordinal).ToArray();
        var hash = RequestHasher.Compute(command.ShowId, sorted);

        try
        {
            return await tx.RunAsync("reserve", (uow, c) => ReserveLockedAsync(uow, command, definition.Show, sorted, hash, c), ct);
        }
        catch (DuplicateIdempotencyKeyException)
        {
            // Can't happen for two requests of one user (the user lock serializes them), but it's what the unique
            // constraint reports if it ever does: the other request committed first, so its row is there to read.
            var prior = await reservationReads.FindByKeyAsync(command.UserId, command.IdempotencyKey, ct)
                ?? throw new InvariantViolationException("Idempotency key conflict reported, but no reservation has the key.");
            return ReplayOrConflict(prior, hash);
        }
    }

    // Same user, same key: the same request is a replay, anything else reuses the key and is a conflict (D-031, D-033).
    private static ReservationOutcome ReplayOrConflict(Reservation prior, byte[] hash) =>
        prior.RequestHash.AsSpan().SequenceEqual(hash)
            ? new ReservationOutcome.Replayed(prior)
            : new ReservationOutcome.KeyConflict(prior.Id);

    // Runs inside the transaction and may be re-run whole on a transient error, so it must not touch anything but the
    // unit of work. The order of calls is: lock timeout → user lock → key lookup → count held → lock seats → insert → confirm.
    private async Task<TxResult<ReservationOutcome>> ReserveLockedAsync(
        IUnitOfWork uow,
        ReserveSeatsCommand command,
        ShowInfo show,
        string[] sorted,
        byte[] hash,
        CancellationToken ct)
    {
        await uow.SetLockTimeoutAsync(options.Value.LockTimeout, ct);
        await uow.UserLock.AcquireAsync(command.UserId, ct);

        // Under the user lock, so a retry of a request still in flight waits for it and then sees its committed row.
        // A replay wrote nothing, so rolling back is harmless. Checked before the limit: a replay of a request that
        // used up the limit must still replay.
        var prior = await uow.Reservations.FindByKeyAsync(command.UserId, command.IdempotencyKey, ct);
        if (prior is not null)
        {
            return TxResult<ReservationOutcome>.RollbackWith(ReplayOrConflict(prior, hash));
        }

        // Counted under the user lock, so this user's concurrent requests see each other's committed seats and can't
        // jointly exceed the limit (D-035). Counted before the seat locks, so a declined request never waits on them.
        var held = await uow.Seats.CountConfirmedByUserAsync(command.ShowId, command.UserId, ct);
        if (held + sorted.Length > show.PerUserLimit)
        {
            return TxResult<ReservationOutcome>.RollbackWith(new ReservationOutcome.PerUserLimit(show.PerUserLimit, held, sorted.Length));
        }

        var locked = await uow.Seats.LockForUpdateAsync(command.ShowId, sorted, ct);
        var unavailable = locked.Where(s => s.Status != SeatStatus.Available).Select(s => s.Label).ToArray();
        if (unavailable.Length > 0)
        {
            return TxResult<ReservationOutcome>.RollbackWith(new ReservationOutcome.SeatTaken(unavailable));
        }

        var reservation = Reservation.Confirmed(
            ids.NewId(),
            command.ShowId,
            command.UserId,
            command.IdempotencyKey,
            hash,
            sorted,
            checked(show.PricePaise * sorted.Length),
            clock.UtcNow);
        await uow.Reservations.InsertAsync(reservation, ct);

        // Every seat was locked and available, so anything but a full update means the locks didn't hold.
        var confirmed = await uow.Seats.ConfirmAsync(command.ShowId, sorted, command.UserId, reservation.Id, ct);
        if (confirmed != sorted.Length)
        {
            throw new InvariantViolationException(
                $"Confirmed {confirmed} of {sorted.Length} locked seats for show {command.ShowId}.");
        }

        return TxResult<ReservationOutcome>.CommitWith(new ReservationOutcome.Created(reservation));
    }
}
