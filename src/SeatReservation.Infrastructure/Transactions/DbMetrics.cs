using System.Diagnostics;
using Prometheus;

namespace SeatReservation.Infrastructure.Transactions;

/// <summary>
/// Database metrics of the repository layer (lld §9): latency per operation, errors by kind, transaction retries and
/// <c>db_up</c>. <c>operation</c> is a small fixed set (<see cref="Operations"/> plus the transaction names the services
/// pass to <c>ITransactionRunner</c>: <c>reserve</c>, <c>cancel</c>, <c>create_show</c>), never an id.
/// </summary>
public sealed class DbMetrics
{
    private static readonly double[] Buckets = [.0005, .001, .0025, .005, .01, .025, .05, .1, .25, .5, 1, 2.5, 5, 10, 15];

    private readonly Histogram _duration;
    private readonly Counter _errors;
    private readonly Counter _retries;
    private readonly Gauge _up;

    public DbMetrics(IMetricFactory factory)
    {
        _duration = factory.CreateHistogram(
            "db_query_duration_seconds", "Duration of one database round trip or transaction attempt, by operation.",
            new HistogramConfiguration { LabelNames = ["operation"], Buckets = Buckets });
        _errors = factory.CreateCounter(
            "db_errors_total", "Failed database operations by kind (transient, unavailable, bug).",
            new CounterConfiguration { LabelNames = ["operation", "kind"] });
        _retries = factory.CreateCounter(
            "db_transaction_retries_total", "Transactions re-run after a transient or connection failure.",
            new CounterConfiguration { LabelNames = ["operation"] });
        _up = factory.CreateGauge("db_up", "1 if the last ops-pool probe (readiness or seat gauges) reached the database, else 0.");
    }

    public void ObserveDuration(string operation, TimeSpan elapsed) => _duration.WithLabels(operation).Observe(elapsed.TotalSeconds);

    public void Retried(string operation) => _retries.WithLabels(operation).Inc();

    public void SetUp(bool up) => _up.Set(up ? 1 : 0);

    /// <summary>
    /// Counts a failure under its kind. An idempotency race (a normal replay path) and a cancellation (the caller or
    /// shutdown) are not database errors and are not counted.
    /// </summary>
    public void Failed(string operation, Exception exception)
    {
        var kind = PgErrorClassifier.Classify(exception) switch
        {
            DbErrorKind.Transient => "transient",
            DbErrorKind.Unavailable => "unavailable",
            DbErrorKind.Bug => "bug",
            _ => null,
        };

        if (kind is not null)
        {
            _errors.WithLabels(operation, kind).Inc();
        }
    }

    /// <summary>Times one autocommit read and counts its failure, if any. The exception is rethrown unchanged.</summary>
    public async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> query)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await query();
        }
        catch (Exception ex)
        {
            Failed(operation, ex);
            throw;
        }
        finally
        {
            ObserveDuration(operation, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>The <c>operation</c> label values used outside transactions.</summary>
    public static class Operations
    {
        public const string GetShow = "get_show";
        public const string GetReservation = "get_reservation";
        public const string FastPath = "fast_path";
        public const string Gauges = "gauges";
        public const string Health = "health";
    }
}
