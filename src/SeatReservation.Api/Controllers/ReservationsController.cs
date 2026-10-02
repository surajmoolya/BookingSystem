using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Mapping;
using SeatReservation.Application.Reservations;

namespace SeatReservation.Api.Controllers;

/// <summary>
/// Owner-only reads and cancels (lld §5.5, §5.6). Ids are taken as strings so a malformed one is the same 404
/// <c>reservation_not_found</c> problem as an unknown one, as in <see cref="ShowsController"/>.
/// </summary>
[ApiController]
[Route("reservations")]
[Authorize]
public sealed class ReservationsController(
    ReservationService reservations,
    CancellationService cancellations,
    IHostApplicationLifetime lifetime) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        if (!Guid.TryParse(id, out var reservationId))
        {
            return OutcomeHttpMapper.ReservationNotFound(this);
        }

        var outcome = await reservations.GetForOwnerAsync(reservationId, User.GetUserId(), HttpContext.RequestAborted);
        return OutcomeHttpMapper.ToResult(outcome, this);
    }

    /// <summary>Idempotent: cancelling an already cancelled reservation is a 200 with the same body.</summary>
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(string id)
    {
        if (!Guid.TryParse(id, out var reservationId))
        {
            return OutcomeHttpMapper.ReservationNotFound(this);
        }

        // The app-stopping token, not RequestAborted: a client giving up must not cancel the cancel mid-commit (D-042).
        var outcome = await cancellations.CancelAsync(new CancelReservationCommand(reservationId, User.GetUserId()), lifetime.ApplicationStopping);
        return OutcomeHttpMapper.ToResult(outcome, this);
    }
}
