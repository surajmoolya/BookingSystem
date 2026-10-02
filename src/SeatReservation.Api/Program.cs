using SeatReservation.Api.Health;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program
{
}
