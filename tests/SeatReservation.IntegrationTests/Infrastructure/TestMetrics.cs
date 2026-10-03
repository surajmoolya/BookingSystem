using Prometheus;
using SeatReservation.Infrastructure.Transactions;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>Metrics for repository-layer tests that build components by hand: each call gets its own registry, so nothing is shared.</summary>
public static class TestMetrics
{
    public static DbMetrics NewDb() => new(Metrics.WithCustomRegistry(Metrics.NewCustomRegistry()));
}
