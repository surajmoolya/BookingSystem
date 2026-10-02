using SeatReservation.Application.Auth;

namespace SeatReservation.Application.Abstractions;

public interface ITokenIssuer
{
    IssuedToken Issue(string userId);
}
