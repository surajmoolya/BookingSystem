using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Reservations;
using SeatReservation.Infrastructure.Persistence;
using static SeatReservation.Infrastructure.Repositories.ReservationMapping;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>Autocommit reservation reads on the main pool, outside any transaction (lld §6.3).</summary>
public sealed class ReservationReadRepository(DataSources dataSources) : IReservationReadRepository
{
    private const string OwnersSql = "SELECT label, status, user_id FROM seats WHERE show_id = $1 AND label = ANY($2)";

    public async Task<Reservation?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct)
    {
        await using var command = dataSources.Main.CreateCommand(ByKeySql);
        command.Parameters.Add(Text(userId));
        command.Parameters.Add(Text(idempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await ReadSingleAsync(reader, ct);
    }

    public async Task<Reservation?> GetByIdAsync(Guid reservationId, CancellationToken ct)
    {
        await using var command = dataSources.Main.CreateCommand(ByIdSql);
        command.Parameters.Add(Uuid(reservationId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await ReadSingleAsync(reader, ct);
    }

    /// <summary>
    /// Both selects in one <see cref="NpgsqlBatch"/>, so one round trip (D-088). Outside a transaction each statement
    /// sees its own snapshot. That's fine: the fast path only ever declines, and the locked path re-checks everything.
    /// </summary>
    public async Task<FastPathSnapshot> GetFastPathSnapshotAsync(
        string userId,
        string idempotencyKey,
        Guid showId,
        IReadOnlyList<string> labels,
        CancellationToken ct)
    {
        await using var batch = dataSources.Main.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand(ByKeySql) { Parameters = { Text(userId), Text(idempotencyKey) } });
        batch.BatchCommands.Add(new NpgsqlBatchCommand(OwnersSql) { Parameters = { Uuid(showId), TextArray(labels) } });

        await using var reader = await batch.ExecuteReaderAsync(ct);
        var existing = await ReadSingleAsync(reader, ct);

        await reader.NextResultAsync(ct);
        var owners = new List<SeatOwner>(labels.Count);
        while (await reader.ReadAsync(ct))
        {
            owners.Add(new SeatOwner(
                reader.GetString(0),
                SeatStatusMapping.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return new FastPathSnapshot(existing, owners);
    }
}
