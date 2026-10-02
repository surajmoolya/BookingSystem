using SeatReservation.Api.Auth;
using SeatReservation.Api.Errors;
using SeatReservation.Api.Health;
using SeatReservation.Api.Json;
using SeatReservation.Api.Options;
using SeatReservation.Application;
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
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddValidatedOptions(builder.Configuration);
builder.Services.AddJwtAuth();
builder.Services.AddReadinessChecks();
builder.Services.AddHostedService<ShutdownDrain>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthEndpoints();

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program
{
}
