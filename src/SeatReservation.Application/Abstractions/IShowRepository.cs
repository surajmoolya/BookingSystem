using SeatReservation.Application.Shows;

namespace SeatReservation.Application.Abstractions;

public interface IShowRepository
{
    /// <summary>Inserts the show and all its seats (ordinal = position in <paramref name="labels"/>) in the current transaction.</summary>
    Task InsertShowWithSeatsAsync(ShowInfo show, IReadOnlyList<string> labels, CancellationToken ct);
}
