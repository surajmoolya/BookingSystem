using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Logging;
using SeatReservation.Application.Options;

namespace SeatReservation.Application.Shows;

public sealed class ShowService(
    CreateShowValidator validator,
    ShowCatalog catalog,
    IShowReadRepository reads,
    ITransactionRunner tx,
    IIdGenerator ids,
    IClock clock,
    IReadinessState readiness,
    IOptions<ReservationOptions> reservationOptions,
    ILogger<ShowService> logger)
{
    /// <summary>
    /// Validates, then inserts the show and all its seats in one transaction. The catalog only learns about the show once
    /// that transaction has committed, so it can never hold a show the database doesn't have.
    /// </summary>
    public async Task<CreateShowOutcome> CreateAsync(CreateShowCommand command, CancellationToken ct)
    {
        var errors = validator.Validate(command);
        if (!errors.IsValid)
        {
            return new CreateShowOutcome.Invalid(errors.ToDictionary());
        }

        if (!readiness.IsReady)
        {
            throw new NotReadyException();
        }

        var labels = command.Seats!.Select(s => s!).ToArray();
        var show = new ShowInfo(
            ids.NewId(),
            command.Name!,
            command.PricePaise,
            command.PerUserLimit ?? reservationOptions.Value.DefaultPerUserLimit,
            labels.Length);

        await tx.RunAsync("create_show", async (uow, c) =>
        {
            await uow.Shows.InsertShowWithSeatsAsync(show, labels, c);
            return TxResult<bool>.CommitWith(true);
        }, ct);

        catalog.Add(ShowDefinition.Create(show, labels));
        logger.ShowCreated(show.Id, show.TotalSeats, show.PerUserLimit);

        var seats = labels.Select(l => new SeatState(l, SeatStatus.Available)).ToArray();
        var counts = new SeatCounts(Total: seats.Length, Available: seats.Length, Held: 0, Confirmed: 0);
        return new CreateShowOutcome.Created(new ShowSnapshot(show, counts, seats, clock.UtcNow));
    }

    /// <summary>
    /// Metadata from the catalog, seat state from one single-statement snapshot (D-039), counts aggregated here. The counts
    /// reconcile by construction; if they ever don't, it's logged as <c>invariant.violation</c> and the state is still returned.
    /// </summary>
    public async Task<GetShowOutcome> GetStateAsync(Guid showId, CancellationToken ct)
    {
        if (!readiness.IsReady)
        {
            throw new NotReadyException();
        }

        var definition = await catalog.GetAsync(showId, ct);
        if (definition is null)
        {
            return new GetShowOutcome.ShowNotFound();
        }

        var seats = await reads.GetSeatSnapshotAsync(showId, ct);
        var asOf = clock.UtcNow;

        int available = 0, held = 0, confirmed = 0;
        foreach (var seat in seats)
        {
            switch (seat.Status)
            {
                case SeatStatus.Available: available++; break;
                case SeatStatus.Held: held++; break;
                case SeatStatus.Confirmed: confirmed++; break;
            }
        }

        var counts = new SeatCounts(Total: seats.Count, Available: available, Held: held, Confirmed: confirmed);
        if (available + held + confirmed != counts.Total || counts.Total != definition.Show.TotalSeats)
        {
            logger.InvariantViolation(showId, counts.Total, available, held, confirmed, definition.Show.TotalSeats);
        }

        return new GetShowOutcome.Found(new ShowSnapshot(definition.Show, counts, seats, asOf));
    }
}
