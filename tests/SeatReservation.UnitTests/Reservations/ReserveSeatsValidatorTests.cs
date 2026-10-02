using SeatReservation.Application.Options;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;

namespace SeatReservation.UnitTests.Reservations;

public class ReserveSeatsValidatorTests
{
    private const int MaxSeats = 3;

    private static readonly ShowDefinition Show = ShowDefinition.Create(
        new ShowInfo(Guid.NewGuid(), "friday-night", 25_000, 4, 5),
        ["A1", "A2", "A3", "B1", "B2"]);

    private readonly ReserveSeatsValidator _validator =
        new(Microsoft.Extensions.Options.Options.Create(new ReservationOptions { MaxSeatsPerRequest = MaxSeats }));

    private static ReserveSeatsCommand Command(IReadOnlyList<string?>? seats = null, string key = "key-1") =>
        new(Show.Show.Id, "alice", seats ?? ["A1", "A2"], key);

    private IReadOnlyDictionary<string, string[]> ValidationErrors(ReserveSeatsCommand command) =>
        Assert.IsType<ReservationOutcome.ValidationFailed>(_validator.Validate(command, Show)).Errors;

    private void AssertOnlyFieldFails(ReserveSeatsCommand command, string field) =>
        Assert.Equal([field], ValidationErrors(command).Keys);

    [Fact]
    public void Valid_command_passes()
    {
        Assert.Null(_validator.Validate(Command(), Show));
    }

    // ---- seats ----

    [Fact]
    public void Empty_seats_fail() => AssertOnlyFieldFails(Command(seats: []), "Seats");

    [Fact]
    public void Seat_count_is_capped_by_MaxSeatsPerRequest()
    {
        Assert.Null(_validator.Validate(Command(seats: ["A1", "A2", "A3"]), Show));
        AssertOnlyFieldFails(Command(seats: ["A1", "A2", "A3", "B1"]), "Seats");
    }

    [Fact]
    public void Duplicate_seats_fail_and_are_named()
    {
        var errors = ValidationErrors(Command(seats: ["A1", "A1"]));

        Assert.Equal(["Seats"], errors.Keys);
        Assert.Contains("A1", Assert.Single(errors["Seats"]));
    }

    [Fact]
    public void Duplicates_are_case_sensitive()
    {
        // "a1" is not a duplicate of "A1"; it is an unknown seat instead.
        var unknown = Assert.IsType<ReservationOutcome.UnknownSeat>(_validator.Validate(Command(seats: ["A1", "a1"]), Show));
        Assert.Equal(["a1"], unknown.UnknownSeats);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_labels_fail(string? label) => AssertOnlyFieldFails(Command(seats: ["A1", label]), "Seats");

    // ---- idempotency key ----

    [Fact]
    public void Empty_key_fails() => AssertOnlyFieldFails(Command(key: ""), "IdempotencyKey");

    [Fact]
    public void Key_bounds_are_1_to_128_characters()
    {
        Assert.Null(_validator.Validate(Command(key: "k"), Show));
        Assert.Null(_validator.Validate(Command(key: new string('k', 128)), Show));
        AssertOnlyFieldFails(Command(key: new string('k', 129)), "IdempotencyKey");
    }

    [Theory]
    [InlineData("0b9e5c4e-7a7f-4c62-9a43-3f0e2a1d9c11")]
    [InlineData("01HZY3K2M8Q4ZK7X9V5R6T1B2C")]
    [InlineData("aGVsbG8+d29ybGQ/Pw==")]
    [InlineData("order:42/retry#1")]
    [InlineData("!~")]
    public void Printable_ascii_keys_pass(string key) => Assert.Null(_validator.Validate(Command(key: key), Show));

    [Theory]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("line\n")]
    [InlineData("nul\0")]
    [InlineData("del\u007f")]
    [InlineData("café")]
    [InlineData("emoji-🎟")]
    public void Keys_with_bad_characters_fail(string key) => AssertOnlyFieldFails(Command(key: key), "IdempotencyKey");

    [Fact]
    public void Every_bad_field_is_reported_together()
    {
        Assert.Equal(["IdempotencyKey", "Seats"], ValidationErrors(Command(seats: [], key: "")).Keys.Order());
    }

    // ---- unknown seats ----

    [Fact]
    public void Labels_not_in_the_show_return_UnknownSeat_listing_all_of_them()
    {
        var unknown = Assert.IsType<ReservationOutcome.UnknownSeat>(_validator.Validate(Command(seats: ["Z9", "A1", "Z8"]), Show));

        Assert.Equal(["Z9", "Z8"], unknown.UnknownSeats);
    }

    [Fact]
    public void Shape_errors_win_over_unknown_seats()
    {
        // A malformed request is a 400 validation, even if it also names seats the show doesn't have.
        AssertOnlyFieldFails(Command(seats: ["Z9", "Z9"]), "Seats");
        AssertOnlyFieldFails(Command(seats: ["Z9"], key: ""), "IdempotencyKey");
    }
}
