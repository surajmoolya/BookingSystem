using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Contracts.Requests;
using SeatReservation.Api.Mapping;
using SeatReservation.Application.Auth;

namespace SeatReservation.Api.Controllers;

[ApiController]
[Route("auth")]
[AllowAnonymous]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>Demo login (D-020): a valid username gets a bearer token whose <c>sub</c> is that username.</summary>
    [HttpPost("token")]
    public IActionResult Token(TokenRequest body) =>
        OutcomeHttpMapper.ToResult(auth.IssueToken(body.Username), this);
}
