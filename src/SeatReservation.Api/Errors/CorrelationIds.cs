namespace SeatReservation.Api.Errors;

public static class CorrelationIds
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    /// <summary>
    /// Records the request's correlation id and arranges for it to be sent as <see cref="HeaderName"/>. The id lives in
    /// <c>HttpContext.Items</c>, and the header is written in <c>OnStarting</c>, because the exception-handler middleware
    /// clears the response (headers included) before it runs the handler: a header set up front would be lost on exactly the
    /// error responses where it is most needed. Called by the correlation middleware (T-5.9).
    /// </summary>
    public static void Set(HttpContext context, string correlationId)
    {
        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(
            static state =>
            {
                var (httpContext, id) = ((HttpContext, string))state;
                httpContext.Response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            },
            (context, correlationId));
    }

    /// <summary>The id to quote in <c>correlation_id</c>: the one set by the middleware, else the request's trace identifier.</summary>
    public static string For(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id && id.Length > 0
            ? id
            : context.TraceIdentifier;
}
