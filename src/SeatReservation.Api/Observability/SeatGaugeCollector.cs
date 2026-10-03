using System.Diagnostics;
using Microsoft.Extensions.Options;
using Prometheus;
using SeatReservation.Api.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Shows;

namespace SeatReservation.Api.Observability;

/// <summary>
/// Seat gauges read from the database at scrape time (lld §9, D-086): per show <c>show_seats{show_id,state}</c> and
/// <c>show_seats_total{show_id}</c> for the <c>Metrics:MaxShowsInGauges</c> most recent shows, plus the global
/// <c>seats_*</c> totals. The query result is reused for <c>Metrics:SeatGaugeCacheSeconds</c>, so a burst of scrapes
/// costs one query. If the query fails or takes longer than 2 s, the last values stay and <c>seats_gauge_stale</c> is 1;
/// a scrape never fails because of the database.
/// </summary>
public sealed class SeatGaugeCollector
{
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] States = ["available", "held", "confirmed"];

    private readonly ISeatStatisticsQuery _query;
    private readonly IReadinessState _readiness;
    private readonly ILogger<SeatGaugeCollector> _logger;
    private readonly int _maxShows;
    private readonly long _cacheTicks;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private readonly Gauge _showSeats;
    private readonly Gauge _showSeatsTotal;
    private readonly Gauge _available;
    private readonly Gauge _held;
    private readonly Gauge _confirmed;
    private readonly Gauge _total;
    private readonly Gauge _stale;

    private HashSet<Guid> _exportedShows = [];
    private long _refreshedAt;
    private bool _hasData;

    public SeatGaugeCollector(
        IMetricFactory factory,
        CollectorRegistry registry,
        ISeatStatisticsQuery query,
        IReadinessState readiness,
        IOptions<MetricsOptions> options,
        ILogger<SeatGaugeCollector> logger)
    {
        _query = query;
        _readiness = readiness;
        _logger = logger;
        _maxShows = options.Value.MaxShowsInGauges;
        _cacheTicks = (long)(options.Value.SeatGaugeCacheSeconds * (double)Stopwatch.Frequency);

        _showSeats = factory.CreateGauge(
            "show_seats", "Seats of one recent show by state; equals counts in GET /shows/{id}.",
            new GaugeConfiguration { LabelNames = ["show_id", "state"] });
        _showSeatsTotal = factory.CreateGauge(
            "show_seats_total", "Total seats of one recent show.",
            new GaugeConfiguration { LabelNames = ["show_id"] });
        _available = factory.CreateGauge("seats_available", "Available seats across all shows.");
        _held = factory.CreateGauge("seats_held", "Held seats across all shows (always 0: release is an explicit cancel).");
        _confirmed = factory.CreateGauge("seats_confirmed", "Confirmed seats across all shows.");
        _total = factory.CreateGauge("seats_total", "Seats across all shows.");
        _stale = factory.CreateGauge("seats_gauge_stale", "1 when the seat gauges could not be refreshed and show the last known values.");
        _stale.Set(1);   // nothing read yet

        registry.AddBeforeCollectCallback(RefreshAsync);
    }

    private async Task RefreshAsync(CancellationToken scrapeAborted)
    {
        if (!_readiness.IsReady || Fresh())
        {
            return;
        }

        // Concurrent scrapes wait for the one refresh in flight instead of each running the query.
        await _refresh.WaitAsync(scrapeAborted);
        try
        {
            if (Fresh())
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(scrapeAborted);
            timeout.CancelAfter(QueryTimeout);
            try
            {
                Apply(await _query.GetCountsAsync(_maxShows, timeout.Token));
                _refreshedAt = Stopwatch.GetTimestamp();
                _hasData = true;
                _stale.Set(0);
            }
            catch (Exception ex) when (!scrapeAborted.IsCancellationRequested)
            {
                _stale.Set(1);
                _logger.LogWarning("metrics.seat_gauges_stale error={Error}", ex is OperationCanceledException
                    ? $"query exceeded {QueryTimeout.TotalSeconds:0}s"
                    : ex.GetType().Name);
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    private bool Fresh() => _hasData && Stopwatch.GetTimestamp() - _refreshedAt < _cacheTicks;

    private void Apply(SeatStatistics stats)
    {
        _available.Set(stats.Global.Available);
        _held.Set(stats.Global.Held);
        _confirmed.Set(stats.Global.Confirmed);
        _total.Set(stats.Global.Total);

        foreach (var (showId, counts) in stats.PerShow)
        {
            var id = showId.ToString();
            _showSeats.WithLabels(id, States[0]).Set(counts.Available);
            _showSeats.WithLabels(id, States[1]).Set(counts.Held);
            _showSeats.WithLabels(id, States[2]).Set(counts.Confirmed);
            _showSeatsTotal.WithLabels(id).Set(counts.Total);
        }

        // Shows that fell out of the recent set lose their series, so the series count stays bounded by MaxShowsInGauges.
        foreach (var gone in _exportedShows.Where(id => !stats.PerShow.ContainsKey(id)))
        {
            var id = gone.ToString();
            foreach (var state in States)
            {
                _showSeats.RemoveLabelled(id, state);
            }

            _showSeatsTotal.RemoveLabelled(id);
        }

        _exportedShows = [.. stats.PerShow.Keys];
    }
}
