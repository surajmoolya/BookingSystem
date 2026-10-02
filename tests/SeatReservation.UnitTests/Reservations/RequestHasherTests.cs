using SeatReservation.Application.Reservations;

namespace SeatReservation.UnitTests.Reservations;

public class RequestHasherTests
{
    private static readonly Guid Show = Guid.Parse("6f1c0000-0000-0000-0000-000000000001");

    [Fact]
    public void Seat_order_does_not_change_the_hash()
    {
        Assert.Equal(RequestHasher.Compute(Show, ["A1", "B10", "A2"]), RequestHasher.Compute(Show, ["B10", "A2", "A1"]));
    }

    [Fact]
    public void A_different_show_changes_the_hash()
    {
        Assert.NotEqual(RequestHasher.Compute(Show, ["A1"]), RequestHasher.Compute(Guid.NewGuid(), ["A1"]));
    }

    [Fact]
    public void Label_case_changes_the_hash()
    {
        Assert.NotEqual(RequestHasher.Compute(Show, ["A1"]), RequestHasher.Compute(Show, ["a1"]));
    }

    [Fact]
    public void A_different_seat_set_changes_the_hash()
    {
        Assert.NotEqual(RequestHasher.Compute(Show, ["A1", "A2"]), RequestHasher.Compute(Show, ["A1"]));
    }

    [Fact]
    public void Labels_are_sorted_ordinally_not_culturally()
    {
        // Ordinal: uppercase (0x41-0x5A) sorts before lowercase; "A10" before "A2".
        Assert.Equal($"v1|{Show:N}|A10,A2,B1,a1", RequestHasher.Canonicalize(Show, ["a1", "B1", "A2", "A10"]));
    }

    // Golden value: changing the canonical form breaks every stored hash, so it must be a deliberate 'v2'.
    [Fact]
    public void V1_canonical_form_and_hash_are_stable()
    {
        Assert.Equal("v1|6f1c0000000000000000000000000001|A1,A2,B10", RequestHasher.Canonicalize(Show, ["B10", "A1", "A2"]));
        Assert.Equal(
            "5ea7f6827384928f69e35f35f95428678b0edfed341d5c4b616b09cc889024cb",
            Convert.ToHexString(RequestHasher.Compute(Show, ["B10", "A1", "A2"])).ToLowerInvariant());
    }
}
