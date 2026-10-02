namespace SeatReservation.Api.Contracts.Responses;

/// <summary><c>POST /auth/token</c> 200 body; <see cref="ExpiresIn"/> is in seconds (lld §5.1).</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, long ExpiresIn, string UserId);
