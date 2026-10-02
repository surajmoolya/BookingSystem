using System.Collections.Frozen;

namespace SeatReservation.Application.Shows;

/// <summary>
/// What <see cref="ShowCatalog"/> caches: immutable show metadata plus the set of seat labels, so a reserve can reject
/// unknown seats without a DB round trip (hld §3). Seat <em>state</em> is never cached.
/// </summary>
public sealed record ShowDefinition(ShowInfo Show, FrozenSet<string> Labels)
{
    public static ShowDefinition Create(ShowInfo show, IEnumerable<string> labels) =>
        new(show, labels.ToFrozenSet(StringComparer.Ordinal));

    public bool HasSeat(string label) => Labels.Contains(label);
}
