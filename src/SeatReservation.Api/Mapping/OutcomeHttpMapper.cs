using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Contracts.Responses;
using SeatReservation.Api.Errors;
using SeatReservation.Application.Auth;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;

namespace SeatReservation.Api.Mapping;

/// <summary>The only place where logic-layer outcomes become HTTP status codes and bodies (lld §3.1).</summary>
public static class OutcomeHttpMapper
{
    public static IActionResult ToResult(IssueTokenOutcome outcome, ControllerBase controller) => outcome switch
    {
        IssueTokenOutcome.Issued issued => controller.Ok(new TokenResponse(
            issued.Token.AccessToken, "Bearer", (long)issued.Token.ExpiresIn.TotalSeconds, issued.UserId)),
        IssueTokenOutcome.Invalid invalid => Validation(controller, invalid.Errors),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    /// <summary>201 with <c>Location: /shows/{id}</c> and the same body as <c>GET /shows/{id}</c>.</summary>
    public static IActionResult ToResult(CreateShowOutcome outcome, ControllerBase controller) => outcome switch
    {
        CreateShowOutcome.Created created => controller.Created(
            $"/shows/{created.Snapshot.Show.Id}", DtoMapper.ToResponse(created.Snapshot)),
        CreateShowOutcome.Invalid invalid => Validation(controller, invalid.Errors),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    public static IActionResult ToResult(GetShowOutcome outcome, ControllerBase controller) => outcome switch
    {
        GetShowOutcome.Found found => controller.Ok(DtoMapper.ToResponse(found.Snapshot)),
        GetShowOutcome.ShowNotFound => ShowNotFound(controller),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>The reserve status table of lld §5.4.</summary>
    public static IActionResult ToResult(ReservationOutcome outcome, ControllerBase controller)
    {
        switch (outcome)
        {
            case ReservationOutcome.Created created:
                return controller.Created($"/reservations/{created.Reservation.Id}", DtoMapper.ToResponse(created.Reservation));

            // 200, not 201: nothing new was created; the header lets a client tell a replay from a fresh success (D-033).
            case ReservationOutcome.Replayed replayed:
                controller.Response.Headers[ReplayedHeader] = "true";
                return controller.Ok(DtoMapper.ToResponse(replayed.Reservation));

            case ReservationOutcome.SeatTaken taken:
                return Problem(controller, StatusCodes.Status409Conflict, ErrorCodes.SeatTaken, new() { ["unavailable_seats"] = taken.UnavailableSeats });

            case ReservationOutcome.PerUserLimit limit:
                return Problem(controller, StatusCodes.Status409Conflict, ErrorCodes.PerUserLimit, new()
                {
                    ["limit"] = limit.Limit,
                    ["held"] = limit.Held,
                    ["requested"] = limit.Requested,
                });

            case ReservationOutcome.KeyConflict conflict:
                return Problem(controller, StatusCodes.Status409Conflict, ErrorCodes.IdempotencyKeyConflict, new() { ["reservation_id"] = conflict.OriginalReservationId });

            case ReservationOutcome.UnknownSeat unknown:
                return Problem(controller, StatusCodes.Status400BadRequest, ErrorCodes.UnknownSeat, new() { ["unknown_seats"] = unknown.UnknownSeats });

            case ReservationOutcome.ValidationFailed invalid:
                return Validation(controller, invalid.Errors);

            case ReservationOutcome.ShowNotFound:
                return ShowNotFound(controller);

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    public static ObjectResult ShowNotFound(ControllerBase controller) =>
        ProblemFactory.ToResult(ProblemFactory.Create(controller.HttpContext, StatusCodes.Status404NotFound, ErrorCodes.ShowNotFound));

    /// <summary>400 <c>validation</c> for errors already keyed by wire name (e.g. from <see cref="DtoMapper"/>).</summary>
    public static ObjectResult WireValidation(ControllerBase controller, IReadOnlyDictionary<string, string[]> errors) =>
        ProblemFactory.ToResult(ProblemFactory.Validation(controller.HttpContext, errors));

    private static ObjectResult Problem(ControllerBase controller, int status, string code, Dictionary<string, object?> extensions) =>
        ProblemFactory.ToResult(ProblemFactory.Create(controller.HttpContext, status, code, extensions: extensions));

    /// <summary>400 <c>validation</c>; logic-layer field names (<c>PricePaise</c>) become wire names (<c>price_paise</c>).</summary>
    private static ObjectResult Validation(ControllerBase controller, IReadOnlyDictionary<string, string[]> errors) =>
        ProblemFactory.ToResult(ProblemFactory.Validation(
            controller.HttpContext,
            errors.ToDictionary(e => ModelStateErrors.NormalizeKey(e.Key), e => e.Value)));
}
