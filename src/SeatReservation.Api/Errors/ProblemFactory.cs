using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace SeatReservation.Api.Errors;

/// <summary>Builds RFC 7807 problem documents with the project's extension members: <c>code</c> and <c>correlation_id</c> on all, plus code-specific ones.</summary>
public static class ProblemFactory
{
    public const string ContentType = "application/problem+json";

    private static readonly Dictionary<string, string> Titles = new()
    {
        [ErrorCodes.Validation] = "One or more validation errors occurred.",
        [ErrorCodes.UnknownSeat] = "One or more seats do not exist in this show.",
        [ErrorCodes.ShowNotFound] = "The show was not found.",
        [ErrorCodes.ReservationNotFound] = "The reservation was not found.",
        [ErrorCodes.SeatTaken] = "One or more seats are not available.",
        [ErrorCodes.PerUserLimit] = "The per-user seat limit would be exceeded.",
        [ErrorCodes.IdempotencyKeyConflict] = "The idempotency key was already used for a different request.",
        [ErrorCodes.NotOwner] = "The reservation belongs to another user.",
        [ErrorCodes.DependencyUnavailable] = "A required dependency is temporarily unavailable.",
        [ErrorCodes.NotReady] = "The service is starting up.",
        [ErrorCodes.Overloaded] = "The service is overloaded.",
        [ErrorCodes.BadRequest] = "The request could not be processed.",
        [ErrorCodes.PayloadTooLarge] = "The request body is too large.",
        [ErrorCodes.InternalError] = "An unexpected error occurred.",
    };

    public static ProblemDetails Create(
        HttpContext context,
        int status,
        string code,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = Titles.GetValueOrDefault(code, code),
            Detail = detail,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlation_id"] = CorrelationIds.For(context);
        foreach (var (key, value) in extensions ?? new Dictionary<string, object?>())
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    /// <summary>400 <c>validation</c> with an <c>errors</c> map of field name to messages (names already snake_case).</summary>
    public static ProblemDetails Validation(HttpContext context, IReadOnlyDictionary<string, string[]> errors) =>
        Create(context, StatusCodes.Status400BadRequest, ErrorCodes.Validation,
            extensions: new Dictionary<string, object?> { ["errors"] = errors });

    /// <summary>The problem as an MVC result, for controller actions and <c>InvalidModelStateResponseFactory</c>.</summary>
    public static ObjectResult ToResult(ProblemDetails problem) =>
        new(problem) { StatusCode = problem.Status, ContentTypes = { ContentType } };

    /// <summary>Writes the problem straight to the response, for code that runs outside MVC (exception handler, rate limiter).</summary>
    public static Task WriteAsync(HttpContext context, ProblemDetails problem, CancellationToken ct = default)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return context.Response.WriteAsJsonAsync(problem, options, ContentType, ct);
    }
}
