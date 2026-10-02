using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;
using SeatReservation.Application.Validation;

namespace SeatReservation.Application.Shows;

/// <summary><c>POST /shows</c> rules (lld §5.2). Every problem is reported, not just the first.</summary>
public sealed partial class CreateShowValidator(IOptions<ShowOptions> options)
{
    public const int MaxNameLength = 200;
    public const int MaxLabelLength = 16;

    /// <summary>How many offending labels a message lists; a 10,000-seat request could otherwise echo all of them.</summary>
    public const int MaxLabelsInMessage = 10;

    public ValidationErrors Validate(CreateShowCommand command)
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            errors.Add(nameof(command.Name), "The name is required.");
        }
        else if (command.Name.Length > MaxNameLength)
        {
            errors.Add(nameof(command.Name), $"The name must be at most {MaxNameLength} characters.");
        }

        ValidateSeats(command.Seats, errors);

        if (command.PricePaise < 0)
        {
            errors.Add(nameof(command.PricePaise), "The price must be zero or more paise.");
        }

        if (command.PerUserLimit is { } limit and (< 1 or > ReservationOptionsValidator.MaxPerUserLimit))
        {
            errors.Add(nameof(command.PerUserLimit), $"The per-user limit must be between 1 and {ReservationOptionsValidator.MaxPerUserLimit}.");
        }

        return errors;
    }

    private void ValidateSeats(IReadOnlyList<string?>? seats, ValidationErrors errors)
    {
        const string field = nameof(CreateShowCommand.Seats);
        var maxSeats = options.Value.MaxSeats;

        if (seats is null || seats.Count == 0)
        {
            errors.Add(field, "At least one seat is required.");
            return;
        }

        if (seats.Count > maxSeats)
        {
            errors.Add(field, $"A show can have at most {maxSeats} seats.");
            return;
        }

        var invalid = seats.Where(s => s is null || !LabelPattern().IsMatch(s)).Select(s => s ?? "null").Distinct(StringComparer.Ordinal).ToList();
        if (invalid.Count > 0)
        {
            errors.Add(field, $"Seat labels must be 1-{MaxLabelLength} characters of A-Z, a-z, 0-9 or '-'. Invalid: {Sample(invalid)}.");
        }

        var duplicates = seats.OfType<string>().GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            errors.Add(field, $"Seat labels must be unique (case-sensitive). Duplicated: {Sample(duplicates)}.");
        }
    }

    private static string Sample(IReadOnlyList<string> labels) =>
        labels.Count <= MaxLabelsInMessage
            ? string.Join(", ", labels)
            : $"{string.Join(", ", labels.Take(MaxLabelsInMessage))} and {labels.Count - MaxLabelsInMessage} more";

    // \z, not $: '$' would also accept a trailing '\n'.
    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();
}
