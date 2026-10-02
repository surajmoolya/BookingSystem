using Npgsql;
using NpgsqlTypes;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Reservations;
using SeatReservation.Infrastructure.Transactions;
using static SeatReservation.Infrastructure.Repositories.ReservationMapping;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>Reservation reads and writes inside the caller's transaction (lld §6.3). No business decisions.</summary>
public sealed class ReservationRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : IReservationRepository
{
    private const string InsertSql = """
        INSERT INTO reservations (id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status, created_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
        """;

    private const string MarkCancelledSql = "UPDATE reservations SET status = 'cancelled', cancelled_at = $2 WHERE id = $1";

    public async Task<Reservation?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(ByKeySql, connection, transaction) { Parameters = { Text(userId), Text(idempotencyKey) } };
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await ReadSingleAsync(reader, ct);
    }

    public async Task<Reservation?> GetByIdForUpdateAsync(Guid reservationId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"{ByIdSql} FOR UPDATE", connection, transaction) { Parameters = { Uuid(reservationId) } };
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await ReadSingleAsync(reader, ct);
    }

    /// <exception cref="DuplicateIdempotencyKeyException">23505 on <c>uq_reservations_user_key</c>.</exception>
    public async Task InsertAsync(Reservation reservation, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(InsertSql, connection, transaction)
        {
            Parameters =
            {
                Uuid(reservation.Id),
                Uuid(reservation.ShowId),
                Text(reservation.UserId),
                Text(reservation.IdempotencyKey),
                new() { Value = reservation.RequestHash, NpgsqlDbType = NpgsqlDbType.Bytea },
                TextArray(reservation.Seats),
                new() { Value = reservation.AmountPaise, NpgsqlDbType = NpgsqlDbType.Bigint },
                Text(ToText(reservation.Status)),
                Timestamp(reservation.CreatedAt),
            },
        };

        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == PgErrorClassifier.IdempotencyConstraint)
        {
            throw new DuplicateIdempotencyKeyException(ex);
        }
    }

    public async Task MarkCancelledAsync(Guid reservationId, DateTimeOffset at, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(MarkCancelledSql, connection, transaction) { Parameters = { Uuid(reservationId), Timestamp(at) } };
        await command.ExecuteNonQueryAsync(ct);
    }
}
