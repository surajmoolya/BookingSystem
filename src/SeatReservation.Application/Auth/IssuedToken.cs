namespace SeatReservation.Application.Auth;

public sealed record IssuedToken(string AccessToken, TimeSpan ExpiresIn);
