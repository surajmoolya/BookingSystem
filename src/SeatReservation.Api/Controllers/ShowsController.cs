using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SeatReservation.Api.Admission;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Contracts.Requests;
using SeatReservation.Api.Mapping;
using SeatReservation.Api.Observability;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;

namespace SeatReservation.Api.Controllers;

[ApiController]
[Route("shows")]
[EnableRateLimiting(AdmissionControl.DbPolicy)]
public sealed class ShowsController(ShowService shows, ReservationService reservations, IHostApplicationLifetime lifetime) : ControllerBase
{
    /// <summary>A 10,000-seat show with 16-character labels is ~190 KB of JSON, above the global 64 KB body limit (T-6.7).</summary>
    public const long CreateBodyLimitBytes = 256 * 1024;

    /// <summary>Admin operation; anonymous unless <c>Shows:RequireAuth</c> (D-021).</summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.CreateShow)]
    [RequestSizeLimit(CreateBodyLimitBytes)]
    public async Task<IActionResult> Create(CreateShowRequest body)
    {
        if (!DtoMapper.TryToCommand(body, out var command, out var errors))
        {
            return OutcomeHttpMapper.WireValidation(this, errors);
        }

        // The app-stopping token, not RequestAborted: a client timing out must not cancel the transaction halfway (D-042).
        var outcome = await shows.CreateAsync(command, lifetime.ApplicationStopping);
        return OutcomeHttpMapper.ToResult(outcome, this);
    }

    /// <summary>
    /// Takes the id as a string so a malformed one is the same 404 <c>show_not_found</c> problem as an unknown one,
    /// rather than the empty 404 a <c>{id:guid}</c> route constraint gives.
    /// </summary>
    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string id)
    {
        if (!Guid.TryParse(id, out var showId))
        {
            return OutcomeHttpMapper.ShowNotFound(this);
        }

        var outcome = await shows.GetStateAsync(showId, HttpContext.RequestAborted);
        return OutcomeHttpMapper.ToResult(outcome, this);
    }

    /// <summary>
    /// Reserves seats for the token's user; a <c>user_id</c> in the body is ignored (lld §5.4). A malformed show id is the
    /// same 404 as an unknown one, as for <see cref="Get"/>.
    /// </summary>
    [HttpPost("{id}/reserve")]
    [Authorize]
    public async Task<IActionResult> Reserve(
        string id,
        ReserveRequest body,
        [FromHeader(Name = IdempotencyKeyBinder.HeaderName)] string? headerKey)
    {
        if (!Guid.TryParse(id, out var showId))
        {
            return OutcomeHttpMapper.ShowNotFound(this);
        }

        if (!IdempotencyKeyBinder.TryResolve(headerKey, body.IdempotencyKey, out var key, out var errors))
        {
            return OutcomeHttpMapper.WireValidation(this, errors);
        }

        var command = new ReserveSeatsCommand(showId, User.GetUserId(), body.Seats ?? [], key);

        // The app-stopping token, not RequestAborted: a client giving up must not cancel a reservation mid-commit (D-042).
        var outcome = await reservations.ReserveAsync(command, lifetime.ApplicationStopping);
        RequestLogContext.Reserve(this, outcome, command.UserId, showId);
        return OutcomeHttpMapper.ToResult(outcome, this);
    }
}
