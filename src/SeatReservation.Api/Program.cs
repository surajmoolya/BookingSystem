using SeatReservation.Api.Health;
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

builder.Services.AddControllers();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddValidatedOptions(builder.Configuration);

var app = builder.Build();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program
{
}
