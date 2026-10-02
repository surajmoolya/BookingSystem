using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Contracts.Responses;
using SeatReservation.Api.Errors;
using SeatReservation.Application.Auth;

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

    /// <summary>400 <c>validation</c>; logic-layer field names (<c>PricePaise</c>) become wire names (<c>price_paise</c>).</summary>
    private static ObjectResult Validation(ControllerBase controller, IReadOnlyDictionary<string, string[]> errors) =>
        ProblemFactory.ToResult(ProblemFactory.Validation(
            controller.HttpContext,
            errors.ToDictionary(e => ModelStateErrors.NormalizeKey(e.Key), e => e.Value)));
}
