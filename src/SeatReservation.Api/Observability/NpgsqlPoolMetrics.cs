using Prometheus;

namespace SeatReservation.Api.Observability;

/// <summary>
/// Bridges the connection-pool instruments of Npgsql's own <c>System.Diagnostics.Metrics</c> meter (usage by state, max,
/// pending requests, timeouts, create time) into the host's registry (T-5.6, optional). Npgsql's command instruments are
/// left out: <c>db_query_duration_seconds</c> already covers them. The <c>pool_name</c> label is the data source name
/// (<c>seatres-api</c> / <c>seatres-ops</c>), never the connection string.
/// </summary>
public sealed class NpgsqlPoolMetrics : IDisposable
{
    public const string MeterName = "Npgsql";

    private readonly IDisposable _adapter;

    // Both: the factory decides where the series are created (its default is the static registry and wins over Registry),
    // and Registry is where the adapter hooks the before-collect poll of the observable instruments (usage, max, pending).
    public NpgsqlPoolMetrics(CollectorRegistry registry, IMetricFactory factory) =>
        _adapter = MeterAdapter.StartListening(new MeterAdapterOptions
        {
            Registry = registry,
            MetricFactory = factory,
            InstrumentFilterPredicate = instrument =>
                instrument.Meter.Name == MeterName && instrument.Name.StartsWith("db.client.connections.", StringComparison.Ordinal),
        });

    public void Dispose() => _adapter.Dispose();
}
