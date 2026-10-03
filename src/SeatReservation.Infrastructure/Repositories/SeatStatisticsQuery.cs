using Npgsql;
using NpgsqlTypes;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Shows;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Transactions;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>
/// Seat counts for the metrics gauges (lld §9, D-086), on the <b>ops</b> pool so a scrape during a burst never waits
/// behind reserves for a connection. Both statements go in one <see cref="NpgsqlBatch"/>: one round trip.
/// Like the readiness probe, the result updates <c>db_up</c>.
/// </summary>
public sealed class SeatStatisticsQuery(DataSources dataSources, DbMetrics metrics) : ISeatStatisticsQuery
{
    private const string PerShowSql = """
        WITH recent AS (SELECT id FROM shows ORDER BY created_at DESC LIMIT $1)
        SELECT s.show_id, s.status, count(*)::int AS n
        FROM seats s JOIN recent r ON r.id = s.show_id
        GROUP BY s.show_id, s.status
        """;

    private const string GlobalSql = "SELECT status, count(*)::int AS n FROM seats GROUP BY status";

    // A recent show with no seat rows can't exist (a show is inserted with its seats in one transaction),
    // so every show in the recent set appears in the per-show rows.
    public async Task<SeatStatistics> GetCountsAsync(int maxShows, CancellationToken ct)
    {
        try
        {
            var stats = await metrics.MeasureAsync(DbMetrics.Operations.Gauges, () => QueryAsync(maxShows, ct));
            metrics.SetUp(true);
            return stats;
        }
        catch
        {
            metrics.SetUp(false);
            throw;
        }
    }

    private async Task<SeatStatistics> QueryAsync(int maxShows, CancellationToken ct)
    {
        await using var connection = await dataSources.Ops.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(connection)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand(PerShowSql) { Parameters = { new NpgsqlParameter { Value = maxShows, NpgsqlDbType = NpgsqlDbType.Integer } } },
                new NpgsqlBatchCommand(GlobalSql),
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);

        var perShow = new Dictionary<Guid, Tally>();
        while (await reader.ReadAsync(ct))
        {
            var showId = reader.GetGuid(0);
            if (!perShow.TryGetValue(showId, out var tally))
            {
                perShow[showId] = tally = new Tally();
            }

            tally.Add(SeatStatusMapping.Parse(reader.GetString(1)), reader.GetInt32(2));
        }

        await reader.NextResultAsync(ct);
        var global = new Tally();
        while (await reader.ReadAsync(ct))
        {
            global.Add(SeatStatusMapping.Parse(reader.GetString(0)), reader.GetInt32(1));
        }

        return new SeatStatistics(global.ToCounts(), perShow.ToDictionary(p => p.Key, p => p.Value.ToCounts()));
    }

    private sealed class Tally
    {
        private int _available;
        private int _held;
        private int _confirmed;

        public void Add(SeatStatus status, int n)
        {
            switch (status)
            {
                case SeatStatus.Available: _available += n; break;
                case SeatStatus.Held: _held += n; break;
                case SeatStatus.Confirmed: _confirmed += n; break;
            }
        }

        public SeatCounts ToCounts() => new(_available + _held + _confirmed, _available, _held, _confirmed);
    }
}
