using Microsoft.Extensions.Options;

namespace SeatReservation.Application.Options;

/// <summary>
/// Collects configuration problems so every layer's options validator reports all of them at once,
/// with the full configuration key (e.g. <c>Database:MaxPoolSize</c>) in each message.
/// </summary>
public sealed class OptionsFailures
{
    private readonly List<string> _failures = [];

    public OptionsFailures InRange(string key, long value, long min, long max)
    {
        if (value < min || value > max)
        {
            _failures.Add($"{key} must be between {min} and {max} but was {value}.");
        }

        return this;
    }

    public OptionsFailures NotBlank(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _failures.Add($"{key} must not be empty.");
        }

        return this;
    }

    public OptionsFailures Add(string message)
    {
        _failures.Add(message);
        return this;
    }

    public ValidateOptionsResult ToResult() =>
        _failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(_failures);
}
