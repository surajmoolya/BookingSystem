using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Infrastructure.Repositories;

namespace SeatReservation.Infrastructure.Transactions;

/// <summary>
/// The repositories bound to one connection and one transaction. The runner creates one per attempt and disposes it with
/// the transaction. The repository properties are filled in by the tasks that own their SQL
/// (user lock, reservations, seats: T-3.6).
/// </summary>
public sealed class PgUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction) : IUnitOfWork
{
    private ShowRepository? _shows;

    /// <summary>The connection the transaction runs on. Repositories use it with <see cref="Transaction"/>.</summary>
    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction Transaction { get; } = transaction;

    public IUserLock UserLock => throw NotYet("user lock", "T-3.6");

    public IReservationRepository Reservations => throw NotYet("reservation repository", "T-3.6");

    public ISeatRepository Seats => throw NotYet("seat repository", "T-3.6");

    public IShowRepository Shows => _shows ??= new ShowRepository(Connection, Transaction);

    /// <summary>Transaction-local: <c>set_config(..., is_local := true)</c> reverts at commit or rollback, so it never leaks through the pool.</summary>
    public async Task SetLockTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('lock_timeout', @value, true)", Connection, Transaction);
        command.Parameters.AddWithValue("value", $"{(long)timeout.TotalMilliseconds}ms");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NotSupportedException NotYet(string what, string task) =>
        new($"The {what} is implemented in {task}.");
}
