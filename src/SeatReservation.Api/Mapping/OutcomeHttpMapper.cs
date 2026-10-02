using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Contracts.Responses;
using SeatReservation.Api.Errors;
using SeatReservation.Application.Auth;
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

    public static ObjectResult ShowNotFound(ControllerBase controller) =>
        ProblemFactory.ToResult(ProblemFactory.Create(controller.HttpContext, StatusCodes.Status404NotFound, ErrorCodes.ShowNotFound));

    /// <summary>400 <c>validation</c> for errors already keyed by wire name (e.g. from <see cref="DtoMapper"/>).</summary>
    public static ObjectResult WireValidation(ControllerBase controller, IReadOnlyDictionary<string, string[]> errors) =>
        ProblemFactory.ToResult(ProblemFactory.Validation(controller.HttpContext, errors));

    /// <summary>400 <c>validation</c>; logic-layer field names (<c>PricePaise</c>) become wire names (<c>price_paise</c>).</summary>
    private static ObjectResult Validation(ControllerBase controller, IReadOnlyDictionary<string, string[]> errors) =>
        ProblemFactory.ToResult(ProblemFactory.Validation(
            controller.HttpContext,
            errors.ToDictionary(e => ModelStateErrors.NormalizeKey(e.Key), e => e.Value)));
}
