using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Options;

namespace SeatReservation.Application.Shows;

public sealed class ShowService(
    CreateShowValidator validator,
    ShowCatalog catalog,
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
        logger.LogInformation("show.created show_id={ShowId} seats={Seats} per_user_limit={PerUserLimit}", show.Id, show.TotalSeats, show.PerUserLimit);

        var seats = labels.Select(l => new SeatState(l, SeatStatus.Available)).ToArray();
        var counts = new SeatCounts(Total: seats.Length, Available: seats.Length, Held: 0, Confirmed: 0);
        return new CreateShowOutcome.Created(new ShowSnapshot(show, counts, seats, clock.UtcNow));
    }
}
