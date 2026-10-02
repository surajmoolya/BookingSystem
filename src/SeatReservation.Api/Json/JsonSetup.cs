using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeatReservation.Api.Json;

public static class JsonSetup
{
    /// <summary>
    /// snake_case property names and enum values (D-007). Unknown JSON members stay ignored, which is what makes a spoofed
    /// <c>user_id</c> in a request body harmless. Applied to both MVC and minimal-API serializers so problem responses written
    /// outside MVC look identical.
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }
}
