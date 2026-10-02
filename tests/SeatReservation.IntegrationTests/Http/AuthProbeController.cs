using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Auth;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// Test-only protected endpoint (registered through <c>ApiFactory</c>), so bearer validation can be tested before any
/// product endpoint requires a token. Echoes the user id the API would use.
/// </summary>
[ApiController]
[Route("_probe/auth")]
[Authorize]
public sealed class AuthProbeController : ControllerBase
{
    [HttpGet("whoami")]
    public IActionResult WhoAmI() => Ok(new { UserId = User.GetUserId() });
}
