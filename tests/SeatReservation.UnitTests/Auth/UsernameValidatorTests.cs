using SeatReservation.Application.Auth;

namespace SeatReservation.UnitTests.Auth;

public class UsernameValidatorTests
{
    [Theory]
    [InlineData("alice")]
    [InlineData("A")]
    [InlineData("user_01.test-x")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]   // exactly 64
    public void Valid_usernames_pass(string username)
    {
        Assert.True(UsernameValidator.Validate(username).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("alice bob")]
    [InlineData("alice\n")]       // '$' would accept a trailing newline; the pattern anchors with \z
    [InlineData("al!ce")]
    [InlineData("alice@example.com")]
    [InlineData("ålice")]         // non-ASCII letters are not in the allowed set
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]   // 65
    public void Invalid_usernames_are_rejected_on_the_username_field(string? username)
    {
        var errors = UsernameValidator.Validate(username).ToDictionary();

        var messages = Assert.Single(errors);
        Assert.Equal(UsernameValidator.Field, messages.Key);
        Assert.Single(messages.Value);
    }
}
