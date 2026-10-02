using SeatReservation.Application.Validation;

namespace SeatReservation.UnitTests.Validation;

public class ValidationErrorsTests
{
    [Fact]
    public void Empty_is_valid()
    {
        Assert.True(new ValidationErrors().IsValid);
        Assert.Empty(new ValidationErrors().ToDictionary());
    }

    [Fact]
    public void Messages_group_by_field_and_repeats_are_dropped()
    {
        var errors = new ValidationErrors()
            .Add("Seats", "a")
            .Add("Seats", "b")
            .Add("Seats", "a")
            .Add("Name", "c");

        Assert.False(errors.IsValid);
        var map = errors.ToDictionary();
        Assert.Equal(["a", "b"], map["Seats"]);
        Assert.Equal(["c"], map["Name"]);
    }
}
