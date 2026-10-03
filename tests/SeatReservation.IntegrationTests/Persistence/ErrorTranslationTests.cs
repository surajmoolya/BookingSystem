using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Transactions;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Persistence;

/// <summary>Database exceptions on the way up: classified per the retry matrix, and the logic layer only ever sees Application exceptions.</summary>
[Collection(PostgresCollection.Name)]
public class ErrorTranslationTests(PostgresFixture postgres)
{
    private static PostgresException Pg(string sqlState, string? constraint = null) =>
        new("m", "ERROR", "ERROR", sqlState, constraintName: constraint);

    [Theory]
    [InlineData("40P01", DbErrorKind.Transient)]      // deadlock_detected
    [InlineData("40001", DbErrorKind.Transient)]      // serialization_failure
    [InlineData("55P03", DbErrorKind.Transient)]      // lock_not_available
    [InlineData("57014", DbErrorKind.Transient)]      // query_canceled
    [InlineData("57P01", DbErrorKind.Unavailable)]    // admin_shutdown
    [InlineData("57P02", DbErrorKind.Unavailable)]    // crash_shutdown
    [InlineData("57P03", DbErrorKind.Unavailable)]    // cannot_connect_now
    [InlineData("53300", DbErrorKind.Unavailable)]    // too_many_connections
    [InlineData("08006", DbErrorKind.Unavailable)]    // connection_failure
    [InlineData("08001", DbErrorKind.Unavailable)]    // sqlclient_unable_to_establish_sqlconnection
    [InlineData("23514", DbErrorKind.Bug)]            // check_violation
    [InlineData("23503", DbErrorKind.Bug)]            // foreign_key_violation
    [InlineData("42P01", DbErrorKind.Bug)]            // undefined_table
    public void Sql_states_map_to_the_retry_matrix(string sqlState, DbErrorKind expected)
    {
        Assert.Equal(expected, PgErrorClassifier.Classify(Pg(sqlState)));
    }

    [Fact]
    public void Unique_violation_on_the_idempotency_constraint_is_a_race_not_an_error_but_on_any_other_constraint_it_is_a_bug()
    {
        Assert.Equal(DbErrorKind.IdempotencyRace, PgErrorClassifier.Classify(Pg("23505", "uq_reservations_user_key")));
        Assert.Equal(DbErrorKind.Bug, PgErrorClassifier.Classify(Pg("23505", "pk_seats")));
        Assert.Equal(DbErrorKind.Bug, PgErrorClassifier.Classify(Pg("23505")));
        Assert.Equal(DbErrorKind.IdempotencyRace, PgErrorClassifier.Classify(new DuplicateIdempotencyKeyException()));
    }

    [Fact]
    public void Network_failures_pool_timeouts_and_cancellation_are_recognised_however_deeply_wrapped()
    {
        Assert.Equal(DbErrorKind.Unavailable, PgErrorClassifier.Classify(new NpgsqlException("connect", new SocketException())));
        Assert.Equal(DbErrorKind.Unavailable, PgErrorClassifier.Classify(new NpgsqlException("pool exhausted", new TimeoutException())));
        Assert.Equal(DbErrorKind.Unavailable, PgErrorClassifier.Classify(new NpgsqlException("io", new IOException())));
        Assert.Equal(DbErrorKind.Unavailable, PgErrorClassifier.Classify(new TimeoutException()));
        Assert.Equal(DbErrorKind.Unavailable, PgErrorClassifier.Classify(new InvalidOperationException("outer", new NpgsqlException("x", new SocketException()))));
        Assert.Equal(DbErrorKind.Cancelled, PgErrorClassifier.Classify(new OperationCanceledException()));
        Assert.Equal(DbErrorKind.Cancelled, PgErrorClassifier.Classify(new TaskCanceledException()));
        Assert.Equal(DbErrorKind.Bug, PgErrorClassifier.Classify(new InvalidOperationException("a bug")));
        Assert.Equal(DbErrorKind.Bug, PgErrorClassifier.Classify(new NpgsqlException("something else")));
    }

    [Fact]
    public void Only_a_cancelled_query_is_limited_to_one_retry()
    {
        Assert.True(PgErrorClassifier.IsQueryCanceled(Pg("57014")));
        Assert.False(PgErrorClassifier.IsQueryCanceled(Pg("40P01")));
        Assert.False(PgErrorClassifier.IsQueryCanceled(new TimeoutException()));
    }

    [Fact]
    public async Task An_unreachable_host_becomes_DependencyUnavailable_after_retries_and_the_delegate_never_runs()
    {
        var options = new DatabaseOptions { MaxPoolSize = 2, OpsPoolSize = 1, ConnectionTimeoutSeconds = 3 };
        await using var sources = DataSources.Create(ApiFactory.UnreachableConnectionString, options, includeErrorDetail: false);
        var runner = new PgTransactionRunner(sources, TestMetrics.NewDb(), NullLogger<PgTransactionRunner>.Instance);
        var ran = false;

        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() => runner.RunAsync<int>("reserve", (uow, ct) =>
        {
            ran = true;
            return Task.FromResult(TxResult<int>.CommitWith(1));
        }, CancellationToken.None));

        Assert.False(ran);
        Assert.IsAssignableFrom<NpgsqlException>(ex.InnerException);
        Assert.Contains("'reserve'", ex.Message);
        Assert.DoesNotContain("Password", ex.ToString());
    }

    [Fact]
    public async Task An_exhausted_pool_becomes_DependencyUnavailable_rather_than_a_raw_Npgsql_timeout()
    {
        var options = new DatabaseOptions { MaxPoolSize = 1, OpsPoolSize = 1, ConnectionTimeoutSeconds = 1 };
        await using var db = await MigratedDatabase.CreateAsync(postgres, options);
        var runner = new PgTransactionRunner(db.Sources, db.Metrics, NullLogger<PgTransactionRunner>.Instance);
        await using var hog = await db.Sources.Main.OpenConnectionAsync();   // takes the only main-pool connection

        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() => runner.RunAsync("test",
            (uow, ct) => Task.FromResult(TxResult<int>.CommitWith(1)), CancellationToken.None));

        Assert.IsAssignableFrom<NpgsqlException>(ex.InnerException);
        Assert.IsType<TimeoutException>(ex.InnerException!.InnerException);
    }

    [Fact]
    public async Task A_duplicate_idempotency_key_insert_surfaces_as_DuplicateIdempotencyKeyException_and_is_not_retried()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await db.InsertShowAsync();
        await db.ExecuteAsync(InsertReservation(showId, Guid.NewGuid(), "alice", "k1"));
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(() =>
            new PgTransactionRunner(db.Sources, db.Metrics, NullLogger<PgTransactionRunner>.Instance).RunAsync<int>("reserve", async (uow, ct) =>
            {
                attempts++;
                var pg = (PgUnitOfWork)uow;
                await using var insert = new NpgsqlCommand(InsertReservation(showId, Guid.NewGuid(), "alice", "k1"), pg.Connection, pg.Transaction);
                await insert.ExecuteNonQueryAsync(ct);
                return TxResult<int>.CommitWith(1);
            }, CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal("uq_reservations_user_key", ((PostgresException)ex.InnerException!).ConstraintName);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM reservations"));
    }

    [Fact]
    public async Task An_application_exception_thrown_by_the_delegate_passes_through_untouched()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var original = new DuplicateIdempotencyKeyException();

        var thrown = await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(() =>
            new PgTransactionRunner(db.Sources, db.Metrics, NullLogger<PgTransactionRunner>.Instance)
                .RunAsync<int>("reserve", (uow, ct) => throw original, CancellationToken.None));

        Assert.Same(original, thrown);
    }

    [Fact]
    public async Task Other_constraint_violations_are_bugs_rethrown_as_the_raw_PostgresException_without_retries()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            new PgTransactionRunner(db.Sources, db.Metrics, NullLogger<PgTransactionRunner>.Instance).RunAsync<int>("test", async (uow, ct) =>
            {
                attempts++;
                var pg = (PgUnitOfWork)uow;
                await using var bad = new NpgsqlCommand("INSERT INTO shows (id, name, price_paise, total_seats) VALUES (gen_random_uuid(), 'x', -1, 1)", pg.Connection, pg.Transaction);
                await bad.ExecuteNonQueryAsync(ct);
                return TxResult<int>.CommitWith(1);
            }, CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(1, attempts);
    }

    private static string InsertReservation(Guid showId, Guid id, string user, string key) =>
        $"INSERT INTO reservations (id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status) " +
        $"VALUES ('{id}', '{showId}', '{user}', '{key}', '\\x00', ARRAY['A1'], 100, 'confirmed')";
}
