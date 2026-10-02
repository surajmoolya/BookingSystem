using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeatReservation.Api.Json;

public static class JsonSetup
{
    /// <summary>
    /// snake_case property names and enum values (D-007). Unknown JSON members stay ignored, which is what makes a spoofed
    /// <c>user_id</c> in a request body harmless. Applied to both MVC and minimal-API serializers so problem responses written
    /// outside MVC look identical. Numbers must be JSON numbers: the Web defaults would also read <c>"25000"</c> as 25000, and a
    /// money field (D-003) should not quietly accept a string.
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }
}
