using Microsoft.AspNetCore.Diagnostics;
using SeatReservation.Application.Exceptions;

namespace SeatReservation.Api.Errors;

/// <summary>
/// The single place where exceptions become HTTP responses. Dependency trouble is an honest 503 with <c>Retry-After</c>
/// (D-056); everything unexpected is a 500 whose body carries a correlation id but never the exception text.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const int RetryAfterSeconds = 1;

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            return false;   // too late to change the status; let the host abort the connection
        }

        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            // The client went away: nothing to report and nobody to report it to.
            logger.LogDebug("request.aborted {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = 499;
            return true;
        }

        var problem = exception switch
        {
            NotReadyException => Unavailable(context, ErrorCodes.NotReady),
            DependencyUnavailableException => Unavailable(context, ErrorCodes.DependencyUnavailable),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                ProblemFactory.Create(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge),
            BadHttpRequestException bad =>
                ProblemFactory.Create(context, bad.StatusCode, ErrorCodes.BadRequest),
            _ => Unexpected(context, exception),
        };

        if (problem.Status == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
            logger.LogWarning(exception, "request.unavailable code={Code} {Method} {Path}", problem.Extensions["code"], context.Request.Method, context.Request.Path);
        }

        await ProblemFactory.WriteAsync(context, problem, cancellationToken);
        return true;
    }

    private static Microsoft.AspNetCore.Mvc.ProblemDetails Unavailable(HttpContext context, string code) =>
        ProblemFactory.Create(context, StatusCodes.Status503ServiceUnavailable, code);

    private Microsoft.AspNetCore.Mvc.ProblemDetails Unexpected(HttpContext context, Exception exception)
    {
        logger.LogError(exception, "unhandled.exception {Method} {Path}", context.Request.Method, context.Request.Path);
        return ProblemFactory.Create(context, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError);
    }
}
