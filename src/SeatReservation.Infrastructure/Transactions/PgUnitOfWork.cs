using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Infrastructure.Repositories;

namespace SeatReservation.Infrastructure.Transactions;

/// <summary>
/// The repositories bound to one connection and one transaction. The runner creates one per attempt and disposes it with
/// the transaction. Repositories are created on first use.
/// </summary>
public sealed class PgUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction) : IUnitOfWork
{
    private UserLock? _userLock;
    private ReservationRepository? _reservations;
    private SeatRepository? _seats;
    private ShowRepository? _shows;

    /// <summary>The connection the transaction runs on. Repositories use it with <see cref="Transaction"/>.</summary>
    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction Transaction { get; } = transaction;

    public IUserLock UserLock => _userLock ??= new UserLock(Connection, Transaction);

    public IReservationRepository Reservations => _reservations ??= new ReservationRepository(Connection, Transaction);

    public ISeatRepository Seats => _seats ??= new SeatRepository(Connection, Transaction);

    public IShowRepository Shows => _shows ??= new ShowRepository(Connection, Transaction);

    /// <summary>Transaction-local: <c>set_config(..., is_local := true)</c> reverts at commit or rollback, so it never leaks through the pool.</summary>
    public async Task SetLockTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('lock_timeout', @value, true)", Connection, Transaction);
        command.Parameters.AddWithValue("value", $"{(long)timeout.TotalMilliseconds}ms");
        await command.ExecuteNonQueryAsync(ct);
    }
}
