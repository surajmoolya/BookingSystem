namespace SeatReservation.Application.Validation;

/// <summary>
/// Field → messages collected by the logic-layer validators. Keys are command property names (e.g. <c>PricePaise</c>);
/// the controller layer turns them into the wire's snake_case names for the <c>errors</c> map of a 400 <c>validation</c>.
/// </summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsValid => _errors.Count == 0;

    public ValidationErrors Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            _errors[field] = messages = [];
        }

        if (!messages.Contains(message))
        {
            messages.Add(message);
        }

        return this;
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
}
