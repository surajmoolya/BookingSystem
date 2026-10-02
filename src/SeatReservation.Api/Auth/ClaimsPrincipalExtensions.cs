using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace SeatReservation.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The caller's user id: the JWT <c>sub</c> claim and nothing else (I7). Only meaningful behind <c>[Authorize]</c>; inbound
    /// claim mapping is off, so <c>sub</c> is not renamed to <see cref="ClaimTypes.NameIdentifier"/>.
    /// </summary>
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value is { Length: > 0 } sub
            ? sub
            : throw new InvalidOperationException("The authenticated principal has no 'sub' claim.");
}
