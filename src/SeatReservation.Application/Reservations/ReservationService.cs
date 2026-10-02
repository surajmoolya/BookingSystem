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

        // Ordinal sort: every transaction takes seat row locks in the same order, so two of them can't deadlock (D-030).
        var sorted = command.Seats.Select(s => s!).Order(StringComparer.Ordinal).ToArray();
        var hash = RequestHasher.Compute(command.ShowId, sorted);

        return await tx.RunAsync("reserve", (uow, c) => ReserveLockedAsync(uow, command, definition.Show, sorted, hash, c), ct);
    }

    // Runs inside the transaction and may be re-run whole on a transient error, so it must not touch anything but the
    // unit of work. The order of calls is: lock timeout → user lock → lock seats → insert → confirm.
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
