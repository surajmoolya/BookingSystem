using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace SeatReservation.Api.Errors;

public static class ModelStateErrors
{
    private const string GenericMessage = "The value is invalid.";
    private const string BodyMessage = "The request body is missing or is not valid JSON.";

    /// <summary>
    /// Flattens model-binding errors into <c>{ "field_name": ["message", …] }</c>. Keys are snake_case to match the wire format;
    /// a body-level problem (empty or malformed JSON) is keyed <c>body</c>. Exception text is never echoed back.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> From(ModelStateDictionary modelState)
    {
        var errors = new Dictionary<string, List<string>>();
        foreach (var (rawKey, entry) in modelState)
        {
            if (entry.Errors.Count == 0)
            {
                continue;
            }

            var key = NormalizeKey(rawKey);
            if (!errors.TryGetValue(key, out var messages))
            {
                errors[key] = messages = [];
            }

            foreach (var error in entry.Errors)
            {
                messages.Add(!string.IsNullOrWhiteSpace(error.ErrorMessage) ? error.ErrorMessage
                    : key == "body" ? BodyMessage
                    : GenericMessage);
            }
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.Distinct().ToArray());
    }

    /// <summary>"" and "$" → body; "$.pricePaise" / "PricePaise" → price_paise; indexers like "seats[0]" are kept.</summary>
    public static string NormalizeKey(string key)
    {
        var trimmed = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
        if (trimmed is "" or "$")
        {
            return "body";
        }

        return string.Join('.', trimmed.Split('.').Select(SnakeCaseSegment));
    }

    private static string SnakeCaseSegment(string segment)
    {
        var bracket = segment.IndexOf('[');
        var name = bracket < 0 ? segment : segment[..bracket];
        var suffix = bracket < 0 ? "" : segment[bracket..];
        return JsonNamingPolicy.SnakeCaseLower.ConvertName(name) + suffix;
    }
}
