namespace SeatReservation.Api.Contracts.Requests;

/// <summary><c>POST /auth/token</c> body. The username is validated in the logic layer (<c>UsernameValidator</c>).</summary>
public sealed class TokenRequest
{
    public string? Username { get; init; }
}
