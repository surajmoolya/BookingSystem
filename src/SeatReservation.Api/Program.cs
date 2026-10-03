using Microsoft.Extensions.DependencyInjection.Extensions;
using SeatReservation.Api.Admission;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Errors;
using SeatReservation.Api.Health;
using SeatReservation.Api.Json;
using SeatReservation.Api.Observability;
using SeatReservation.Api.Options;
using SeatReservation.Application;
using SeatReservation.Application.Abstractions;
using SeatReservation.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Render injects PORT; bind it on all interfaces. Without PORT, honour an explicit ASPNETCORE_URLS
// (launchSettings, tests) and otherwise fall back to 8080, the container's port.
var port = builder.Configuration["PORT"];
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}
else if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

builder.AddStructuredLogging();

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        JsonSetup.Configure(o.JsonSerializerOptions);
        o.AllowInputFormatterExceptionMessages = false;   // never echo parser text (.NET type names, byte offsets) in a 400
    })
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = context =>
        ProblemFactory.ToResult(ProblemFactory.Validation(context.HttpContext, ModelStateErrors.From(context.ModelState))));
builder.Services.ConfigureHttpJsonOptions(o => JsonSetup.Configure(o.SerializerOptions));
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddMetricsRegistry();
builder.Services.AddApplication();
builder.Services.Replace(ServiceDescriptor.Singleton<IReservationMetrics, PrometheusReservationMetrics>());   // over AddApplication's no-op default
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddValidatedOptions(builder.Configuration);
builder.Services.AddJwtAuth();
builder.Services.AddReadinessChecks();
builder.Services.AddAdmissionControl();
builder.Services.AddHostedService<ShutdownDrain>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRequestCompletionLogging();
app.UseExceptionHandler();
app.UseRouting();
app.UseRouteTemplateHttpMetrics();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();   // after auth: a 401 never takes a queue slot
app.MapControllers();
app.MapHealthEndpoints();
app.MapHostMetrics();

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program
{
}
