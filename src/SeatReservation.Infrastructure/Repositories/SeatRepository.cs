using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Reservations;
using static SeatReservation.Infrastructure.Repositories.ReservationMapping;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>Seat reads and writes inside the caller's transaction (lld §6.3). No business decisions.</summary>
public sealed class SeatRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : ISeatRepository
{
    private const string CountConfirmedSql =
        "SELECT count(*)::int FROM seats WHERE show_id = $1 AND user_id = $2 AND status = 'confirmed'";

    // ORDER BY label is mandatory: every transaction then takes the row locks in one global order, which is the deadlock
    // freedom (D-030, lld §6.5). Postgres locks the rows as they come out of the sort. Any fixed collation gives such an
    // order, so this needn't match the logic layer's ordinal sort; nothing relies on the order rows come back in.
    private const string LockSql = """
        SELECT label, status FROM seats
        WHERE show_id = $1 AND label = ANY($2)
        ORDER BY label
        FOR UPDATE
        """;

    private const string ConfirmSql = """
        UPDATE seats SET status = 'confirmed', user_id = $3, reservation_id = $4, updated_at = now()
        WHERE show_id = $1 AND label = ANY($2) AND status = 'available'
        """;

    // Keyed by reservation, so it can never free a seat that belongs to someone else (D-040).
    private const string ReleaseSql = """
        UPDATE seats SET status = 'available', user_id = NULL, reservation_id = NULL, updated_at = now()
        WHERE reservation_id = $1
        """;

    public async Task<int> CountConfirmedByUserAsync(Guid showId, string userId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(CountConfirmedSql, connection, transaction) { Parameters = { Uuid(showId), Text(userId) } };
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<LockedSeat>> LockForUpdateAsync(Guid showId, IReadOnlyList<string> sortedLabels, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(LockSql, connection, transaction) { Parameters = { Uuid(showId), TextArray(sortedLabels) } };
        await using var reader = await command.ExecuteReaderAsync(ct);

        var seats = new List<LockedSeat>(sortedLabels.Count);
        while (await reader.ReadAsync(ct))
        {
            seats.Add(new LockedSeat(reader.GetString(0), SeatStatusMapping.Parse(reader.GetString(1))));
        }

        return seats;
    }

    public async Task<int> ConfirmAsync(Guid showId, IReadOnlyList<string> labels, string userId, Guid reservationId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(ConfirmSql, connection, transaction)
        {
            Parameters = { Uuid(showId), TextArray(labels), Text(userId), Uuid(reservationId) },
        };
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ReleaseByReservationAsync(Guid reservationId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(ReleaseSql, connection, transaction) { Parameters = { Uuid(reservationId) } };
        return await command.ExecuteNonQueryAsync(ct);
    }
}
