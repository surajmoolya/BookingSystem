using Npgsql;
using NpgsqlTypes;
using SeatReservation.Application.Abstractions;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>
/// Per-user transaction advisory lock (D-035), released by Postgres at commit or rollback. The two-key form
/// <c>(1, hashtext(user))</c> lives in a different key space from the migration lock's single bigint key, so the two can
/// never collide. Two users whose names hash alike only serialize with each other: slower, never incorrect.
/// </summary>
public sealed class UserLock(NpgsqlConnection connection, NpgsqlTransaction transaction) : IUserLock
{
    public const int KeySpace = 1;

    private const string Sql = "SELECT pg_advisory_xact_lock($1, hashtext($2))";

    public async Task AcquireAsync(string userId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(Sql, connection, transaction)
        {
            Parameters =
            {
                new() { Value = KeySpace, NpgsqlDbType = NpgsqlDbType.Integer },
                ReservationMapping.Text(userId),
            },
        };
        await command.ExecuteNonQueryAsync(ct);
    }
}
