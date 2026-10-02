using SeatReservation.Application.Auth;
using SeatReservation.UnitTests.Fakes;

namespace SeatReservation.UnitTests.Auth;

public class AuthServiceTests
{
    private readonly FakeTokenIssuer _issuer = new();
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        _service = new AuthService(_issuer);
    }

    [Fact]
    public void Valid_username_gets_a_token_issued_for_exactly_that_user_id()
    {
        var outcome = _service.IssueToken("alice");

        var issued = Assert.IsType<IssueTokenOutcome.Issued>(outcome);
        Assert.Equal("alice", issued.UserId);
        Assert.Equal("token-for-alice", issued.Token.AccessToken);
        Assert.Equal(FakeTokenIssuer.Lifetime, issued.Token.ExpiresIn);
        Assert.Equal(["alice"], _issuer.IssuedFor);
    }

    [Fact]
    public void Username_case_is_preserved_in_the_user_id()
    {
        var issued = Assert.IsType<IssueTokenOutcome.Issued>(_service.IssueToken("Alice"));

        Assert.Equal("Alice", issued.UserId);
        Assert.Equal(["Alice"], _issuer.IssuedFor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("alice\n")]
    public void Invalid_username_is_rejected_and_the_issuer_is_never_called(string? username)
    {
        var outcome = _service.IssueToken(username);

        var invalid = Assert.IsType<IssueTokenOutcome.Invalid>(outcome);
        Assert.Contains(UsernameValidator.Field, invalid.Errors.Keys);
        Assert.Empty(_issuer.IssuedFor);
    }
}
