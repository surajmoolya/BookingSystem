using SeatReservation.Application.Options;
using SeatReservation.Application.Shows;

namespace SeatReservation.UnitTests.Shows;

public class CreateShowValidatorTests
{
    private const int MaxSeats = 5;
    private readonly CreateShowValidator _validator = new(Microsoft.Extensions.Options.Options.Create(new ShowOptions { MaxSeats = MaxSeats }));

    private static CreateShowCommand Valid(
        string? name = "friday-night",
        IReadOnlyList<string?>? seats = null,
        long price = 25_000,
        int? limit = null) =>
        new(name, seats ?? ["A1", "A2", "A3"], price, limit);

    private IReadOnlyDictionary<string, string[]> Errors(CreateShowCommand command) => _validator.Validate(command).ToDictionary();

    private void AssertOnlyFieldFails(CreateShowCommand command, string field)
    {
        var errors = Errors(command);
        Assert.Equal([field], errors.Keys);
    }

    [Fact]
    public void Valid_command_passes()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    // ---- name ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_name_fails(string? name) => AssertOnlyFieldFails(Valid(name: name), "Name");

    [Fact]
    public void Name_bounds_are_1_to_200_characters()
    {
        Assert.True(_validator.Validate(Valid(name: "x")).IsValid);
        Assert.True(_validator.Validate(Valid(name: new string('x', 200))).IsValid);
        AssertOnlyFieldFails(Valid(name: new string('x', 201)), "Name");
    }

    // ---- seats ----

    [Fact]
    public void Missing_or_empty_seats_fail()
    {
        AssertOnlyFieldFails(new CreateShowCommand("n", null, 0, null), "Seats");
        AssertOnlyFieldFails(Valid(seats: []), "Seats");
    }

    [Fact]
    public void Seat_count_is_capped_by_Shows_MaxSeats()
    {
        Assert.True(_validator.Validate(Valid(seats: ["A1", "A2", "A3", "A4", "A5"])).IsValid);
        AssertOnlyFieldFails(Valid(seats: ["A1", "A2", "A3", "A4", "A5", "A6"]), "Seats");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("row-10-seat-99")]
    [InlineData("0123456789abcdef")]   // 16
    public void Valid_labels_pass(string label) => Assert.True(_validator.Validate(Valid(seats: [label])).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("A 1")]
    [InlineData("A_1")]
    [InlineData("A.1")]
    [InlineData("Å1")]
    [InlineData("A1\n")]
    [InlineData("0123456789abcdefg")]   // 17
    [InlineData(null)]
    public void Invalid_labels_fail(string? label) => AssertOnlyFieldFails(Valid(seats: ["A1", label]), "Seats");

    [Fact]
    public void Duplicate_labels_fail_and_the_comparison_is_case_sensitive()
    {
        var errors = Errors(Valid(seats: ["A1", "A2", "A1"]));
        Assert.Contains("A1", Assert.Single(errors["Seats"]));

        Assert.True(_validator.Validate(Valid(seats: ["a1", "A1"])).IsValid);
    }

    [Fact]
    public void Long_lists_of_bad_labels_are_truncated_in_the_message()
    {
        var validator = new CreateShowValidator(Microsoft.Extensions.Options.Options.Create(new ShowOptions { MaxSeats = 100 }));
        var seats = Enumerable.Range(1, 50).Select(i => $"bad_{i}").ToArray();

        var message = Assert.Single(validator.Validate(Valid(seats: seats)).ToDictionary()["Seats"]);

        Assert.Contains("and 40 more", message);
        Assert.DoesNotContain("bad_11", message);
    }

    // ---- price and limit ----

    [Fact]
    public void Price_must_not_be_negative()
    {
        Assert.True(_validator.Validate(Valid(price: 0)).IsValid);
        Assert.True(_validator.Validate(Valid(price: long.MaxValue)).IsValid);
        AssertOnlyFieldFails(Valid(price: -1), "PricePaise");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(100)]
    public void Per_user_limit_is_optional_and_1_to_100(int? limit) => Assert.True(_validator.Validate(Valid(limit: limit)).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Per_user_limit_outside_1_to_100_fails(int limit) => AssertOnlyFieldFails(Valid(limit: limit), "PerUserLimit");

    [Fact]
    public void Every_problem_is_reported_together()
    {
        var errors = Errors(new CreateShowCommand("", ["A1", "A1", "bad label"], -5, 0));

        Assert.Equal(["Name", "PerUserLimit", "PricePaise", "Seats"], errors.Keys.Order());
        Assert.Equal(2, errors["Seats"].Length);   // invalid label and duplicate
    }
}
