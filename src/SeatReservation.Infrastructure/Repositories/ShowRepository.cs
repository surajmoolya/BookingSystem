using Npgsql;
using NpgsqlTypes;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Shows;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>Show writes inside the caller's transaction (lld §5.2).</summary>
public sealed class ShowRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : IShowRepository
{
    private const string InsertShowSql = """
        INSERT INTO shows (id, name, price_paise, per_user_limit, total_seats)
        VALUES ($1, $2, $3, $4, $5)
        """;

    // One statement for all seats: ordinal = 1-based position in the array, so the snapshot keeps the creation order.
    private const string InsertSeatsSql = """
        INSERT INTO seats (show_id, label, ordinal)
        SELECT $1, t.label, t.ord::int
        FROM unnest($2::text[]) WITH ORDINALITY AS t(label, ord)
        """;

    /// <summary>Both inserts go in one <see cref="NpgsqlBatch"/>: a single round trip, whatever the seat count.</summary>
    public async Task InsertShowWithSeatsAsync(ShowInfo show, IReadOnlyList<string> labels, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, transaction)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand(InsertShowSql)
                {
                    Parameters =
                    {
                        new() { Value = show.Id, NpgsqlDbType = NpgsqlDbType.Uuid },
                        new() { Value = show.Name, NpgsqlDbType = NpgsqlDbType.Text },
                        new() { Value = show.PricePaise, NpgsqlDbType = NpgsqlDbType.Bigint },
                        new() { Value = show.PerUserLimit, NpgsqlDbType = NpgsqlDbType.Integer },
                        new() { Value = show.TotalSeats, NpgsqlDbType = NpgsqlDbType.Integer },
                    },
                },
                new NpgsqlBatchCommand(InsertSeatsSql)
                {
                    Parameters =
                    {
                        new() { Value = show.Id, NpgsqlDbType = NpgsqlDbType.Uuid },
                        new() { Value = labels.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
                    },
                },
            },
        };

        await batch.ExecuteNonQueryAsync(ct);
    }
}
