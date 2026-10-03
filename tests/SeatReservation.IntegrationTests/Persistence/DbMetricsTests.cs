using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Prometheus;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Repositories;
using SeatReservation.Infrastructure.Transactions;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Persistence;

/// <summary>Repository-layer DB metrics (T-5.5, lld §9) on a private registry per test.</summary>
[Collection(PostgresCollection.Name)]
public class DbMetricsTests(PostgresFixture postgres)
{
    private readonly CollectorRegistry _registry = Metrics.NewCustomRegistry();

    private DbMetrics NewMetrics() => new(Metrics.WithCustomRegistry(_registry));

    private async Task<MetricsScrape> ScrapeAsync()
    {
        using var stream = new MemoryStream();
        await _registry.CollectAndExportAsTextAsync(stream);
        return MetricsScrape.Parse(Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task A_retried_lock_timeout_counts_one_retry_one_transient_error_and_two_timed_attempts()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var runner = new PgTransactionRunner(db.Sources, NewMetrics(), NullLogger<PgTransactionRunner>.Instance);
        var showId = await db.InsertShowAsync();
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"SELECT 1 FROM shows WHERE id = '{showId}' FOR UPDATE", holder, holderTx))
        {
            await hold.ExecuteNonQueryAsync();
        }

        var attempts = 0;
        await runner.RunAsync("reserve", async (uow, ct) =>
        {
            if (++attempts == 2)
            {
                await holderTx.RollbackAsync(ct);
            }

            await uow.SetLockTimeoutAsync(TimeSpan.FromMilliseconds(100), ct);
            var pg = (PgUnitOfWork)uow;
            await using var lockRow = new NpgsqlCommand($"SELECT 1 FROM shows WHERE id = '{showId}' FOR UPDATE", pg.Connection, pg.Transaction);
            await lockRow.ExecuteNonQueryAsync(ct);
            return TxResult<bool>.RollbackWith(true);
        }, CancellationToken.None);

        var scrape = await ScrapeAsync();
        Assert.Equal(1, scrape.Value("db_transaction_retries_total", ("operation", "reserve")));
        Assert.Equal(1, scrape.Value("db_errors_total", ("operation", "reserve"), ("kind", "transient")));
        Assert.Equal(2, scrape.Value("db_query_duration_seconds_count", ("operation", "reserve")));
    }

    [Fact]
    public async Task An_unreachable_database_counts_unavailable_errors_and_no_bug()
    {
        await using var sources = DataSources.Create(ApiFactory.UnreachableConnectionString, new DatabaseOptions { ConnectionTimeoutSeconds = 2 }, includeErrorDetail: true);
        var runner = new PgTransactionRunner(sources, NewMetrics(), NullLogger<PgTransactionRunner>.Instance);

        await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            runner.RunAsync("cancel", (_, _) => Task.FromResult(TxResult<int>.CommitWith(1)), CancellationToken.None));

        var scrape = await ScrapeAsync();
        Assert.Equal(PgTransactionRunner.MaxAttempts, scrape.Value("db_errors_total", ("operation", "cancel"), ("kind", "unavailable")));
        Assert.Equal(PgTransactionRunner.MaxAttempts - 1, scrape.Value("db_transaction_retries_total", ("operation", "cancel")));
        Assert.Equal(0, scrape.Value("db_errors_total", ("kind", "bug")));
    }

    [Fact]
    public async Task A_bug_in_the_delegate_counts_as_a_bug_and_an_idempotency_race_is_not_an_error()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var runner = new PgTransactionRunner(db.Sources, NewMetrics(), NullLogger<PgTransactionRunner>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync<int>("create_show", (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None));
        await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(() =>
            runner.RunAsync<int>("reserve", (_, _) => throw new DuplicateIdempotencyKeyException(new Exception()), CancellationToken.None));

        var scrape = await ScrapeAsync();
        Assert.Equal(1, scrape.Value("db_errors_total", ("operation", "create_show"), ("kind", "bug")));
        Assert.Equal(0, scrape.Value("db_errors_total", ("operation", "reserve")));
    }

    [Fact]
    public async Task Reads_are_timed_under_their_operation()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var metrics = NewMetrics();
        var shows = new ShowReadRepository(db.Sources, metrics);
        var reservations = new ReservationReadRepository(db.Sources, metrics);
        var showId = await db.InsertShowAsync();

        await shows.GetShowAsync(showId, CancellationToken.None);
        await shows.GetSeatSnapshotAsync(showId, CancellationToken.None);
        await reservations.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);
        await reservations.GetFastPathSnapshotAsync("alice", "k", showId, ["A1"], CancellationToken.None);
        await new SeatStatisticsQuery(db.Sources, metrics).GetCountsAsync(10, CancellationToken.None);

        var scrape = await ScrapeAsync();
        Assert.Equal(2, scrape.Value("db_query_duration_seconds_count", ("operation", "get_show")));
        Assert.Equal(1, scrape.Value("db_query_duration_seconds_count", ("operation", "get_reservation")));
        Assert.Equal(1, scrape.Value("db_query_duration_seconds_count", ("operation", "fast_path")));
        Assert.Equal(1, scrape.Value("db_query_duration_seconds_count", ("operation", "gauges")));
        Assert.Equal(1, scrape.Value("db_up"));
    }

    [Fact]
    public async Task Db_up_drops_to_0_when_the_gauge_query_cannot_reach_the_database()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        await using var down = DataSources.Create(ApiFactory.UnreachableConnectionString, new DatabaseOptions { ConnectionTimeoutSeconds = 2 }, includeErrorDetail: true);
        var metrics = NewMetrics();

        await new SeatStatisticsQuery(db.Sources, metrics).GetCountsAsync(10, CancellationToken.None);
        Assert.Equal(1, (await ScrapeAsync()).Value("db_up"));

        await Assert.ThrowsAnyAsync<NpgsqlException>(() => new SeatStatisticsQuery(down, metrics).GetCountsAsync(10, CancellationToken.None));
        Assert.Equal(0, (await ScrapeAsync()).Value("db_up"));
    }
}
