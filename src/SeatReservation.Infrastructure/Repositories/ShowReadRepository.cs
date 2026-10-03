using Npgsql;
using NpgsqlTypes;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Shows;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Transactions;
using static SeatReservation.Infrastructure.Transactions.DbMetrics.Operations;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>Autocommit show reads on the main pool, outside any transaction (lld §5.3).</summary>
public sealed class ShowReadRepository(DataSources dataSources, PgQueryExecutor reads) : IShowReadRepository
{
    private const string GetShowSql = "SELECT id, name, price_paise, per_user_limit, total_seats FROM shows WHERE id = $1";

    // A single statement reads a single snapshot, so the counts aggregated from it always reconcile (D-039).
    private const string SnapshotSql = "SELECT label, status FROM seats WHERE show_id = $1 ORDER BY ordinal";

    public Task<ShowInfo?> GetShowAsync(Guid showId, CancellationToken ct) =>
        reads.RunAsync(GetShow, attemptCt => QueryShowAsync(showId, attemptCt), ct);

    public Task<IReadOnlyList<SeatState>> GetSeatSnapshotAsync(Guid showId, CancellationToken ct) =>
        reads.RunAsync(GetShow, attemptCt => QuerySnapshotAsync(showId, attemptCt), ct);

    private async Task<ShowInfo?> QueryShowAsync(Guid showId, CancellationToken ct)
    {
        await using var command = dataSources.Main.CreateCommand(GetShowSql);
        command.Parameters.Add(new NpgsqlParameter { Value = showId, NpgsqlDbType = NpgsqlDbType.Uuid });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ShowInfo(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    private async Task<IReadOnlyList<SeatState>> QuerySnapshotAsync(Guid showId, CancellationToken ct)
    {
        await using var command = dataSources.Main.CreateCommand(SnapshotSql);
        command.Parameters.Add(new NpgsqlParameter { Value = showId, NpgsqlDbType = NpgsqlDbType.Uuid });
        await using var reader = await command.ExecuteReaderAsync(ct);

        var seats = new List<SeatState>();
        while (await reader.ReadAsync(ct))
        {
            seats.Add(new SeatState(reader.GetString(0), SeatStatusMapping.Parse(reader.GetString(1))));
        }

        return seats;
    }
}
