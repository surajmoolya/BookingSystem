using Prometheus;
using SeatReservation.Api.Admission;

namespace SeatReservation.Api.Observability;

/// <summary><c>reservation_queue_length</c> (lld §9): requests waiting in the <c>db</c> admission queue, sampled on every scrape.</summary>
public sealed class AdmissionQueueGauge
{
    public AdmissionQueueGauge(IMetricFactory factory, CollectorRegistry registry, DbAdmissionLimiter limiter)
    {
        var gauge = factory.CreateGauge("reservation_queue_length", "Requests waiting in the admission queue for a DB permit.");
        registry.AddBeforeCollectCallback(() => gauge.Set(limiter.QueuedCount));
    }
}
