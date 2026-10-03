using Prometheus;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Reservations;

namespace SeatReservation.Api.Observability;

/// <summary>
/// Maps the logic layer's business metric calls onto prometheus-net counters (lld §9). <em>When</em> they are called
/// (once per final outcome, never on a retried attempt) is the services' job and is unit-tested there.
/// </summary>
public sealed class PrometheusReservationMetrics : IReservationMetrics
{
    private readonly Counter _confirmed;
    private readonly Counter _seatsConfirmed;
    private readonly Counter _cancelled;
    private readonly Counter.Child[] _declined;
    private readonly Histogram.Child[] _duration;

    public PrometheusReservationMetrics(IMetricFactory factory)
    {
        _confirmed = factory.CreateCounter("reservations_confirmed_total", "New reservations committed. Not incremented on replay.");
        _seatsConfirmed = factory.CreateCounter("reservation_seats_confirmed_total", "Seats in new reservations.");
        _cancelled = factory.CreateCounter("reservations_cancelled_total", "Cancellations that changed state (not idempotent repeats).");

        var declined = factory.CreateCounter(
            "reservations_declined_total",
            "Reserve attempts that did not create a reservation, by reason.",
            new CounterConfiguration { LabelNames = ["reason"] });

        // One child per reason, created up front: every reason is exported from the first scrape (as 0),
        // so rate() and increase() work without a missing-series gap, and the label set stays exactly the enum.
        var reasons = Enum.GetValues<DeclineReason>();
        _declined = new Counter.Child[reasons.Length];
        foreach (var reason in reasons)
        {
            _declined[(int)reason] = declined.WithLabels(Label(reason));
        }

        var duration = factory.CreateHistogram(
            "reservation_duration_seconds",
            "Service-level latency of one reserve call, excluding HTTP overhead, by result.",
            new HistogramConfiguration { LabelNames = ["outcome"], Buckets = HttpMetricsSetup.DurationBuckets });
        var kinds = Enum.GetValues<ReservationResultKind>();
        _duration = new Histogram.Child[kinds.Length];
        foreach (var kind in kinds)
        {
            _duration[(int)kind] = duration.WithLabels(kind.ToString().ToLowerInvariant());
        }
    }

    public void Confirmed(int seatCount)
    {
        _confirmed.Inc();
        _seatsConfirmed.Inc(seatCount);
    }

    public void Declined(DeclineReason reason) => _declined[(int)reason].Inc();

    public void Cancelled() => _cancelled.Inc();

    public void ObserveDuration(ReservationResultKind kind, TimeSpan elapsed) => _duration[(int)kind].Observe(elapsed.TotalSeconds);

    /// <summary>The <c>reason</c> label value, matching the problem <c>code</c>s (lld §9).</summary>
    public static string Label(DeclineReason reason) => reason switch
    {
        DeclineReason.SeatTaken => "seat_taken",
        DeclineReason.PerUserLimit => "per_user_limit",
        DeclineReason.IdempotentReplay => "idempotent_replay",
        DeclineReason.IdempotencyKeyConflict => "idempotency_key_conflict",
        DeclineReason.UnknownSeat => "unknown_seat",
        DeclineReason.ShowNotFound => "show_not_found",
        DeclineReason.Validation => "validation",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
