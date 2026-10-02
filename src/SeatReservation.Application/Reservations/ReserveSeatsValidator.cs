using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;
using SeatReservation.Application.Shows;
using SeatReservation.Application.Validation;

namespace SeatReservation.Application.Reservations;

/// <summary>
/// Reserve request rules (lld §5.4). Shape problems (seats, key) are a 400 <c>validation</c> reporting every field;
/// only a well-formed request is checked against the show's labels, giving <see cref="ReservationOutcome.UnknownSeat"/>.
/// </summary>
public sealed class ReserveSeatsValidator(IOptions<ReservationOptions> options)
{
    /// <summary>Matches the CHECK on <c>reservations.idempotency_key</c> (lld §4).</summary>
    public const int MaxKeyLength = 128;

    /// <returns>The declining outcome, or null when the request may proceed.</returns>
    public ReservationOutcome? Validate(ReserveSeatsCommand command, ShowDefinition show)
    {
        var errors = new ValidationErrors();
        ValidateSeats(command.Seats, errors);
        ValidateKey(command.IdempotencyKey, errors);
        if (!errors.IsValid)
        {
            return new ReservationOutcome.ValidationFailed(errors.ToDictionary());
        }

        var unknown = command.Seats.OfType<string>().Where(s => !show.HasSeat(s)).ToArray();
        return unknown.Length > 0 ? new ReservationOutcome.UnknownSeat(unknown) : null;
    }

    private void ValidateSeats(IReadOnlyList<string?> seats, ValidationErrors errors)
    {
        const string field = nameof(ReserveSeatsCommand.Seats);
        var max = options.Value.MaxSeatsPerRequest;

        if (seats.Count == 0)
        {
            errors.Add(field, "At least one seat is required.");
            return;
        }

        if (seats.Count > max)
        {
            errors.Add(field, $"A request can reserve at most {max} seats.");
            return;
        }

        if (seats.Any(string.IsNullOrEmpty))
        {
            errors.Add(field, "Seat labels must not be null or empty.");
        }

        var duplicates = seats.OfType<string>().GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            errors.Add(field, $"Seat labels must be unique (case-sensitive). Duplicated: {string.Join(", ", duplicates)}.");
        }
    }

    // Printable ASCII without space (0x21-0x7E): UUIDs, ULIDs and base64/base64url keys all fit, and nothing a log or
    // header could mangle (control characters, whitespace, non-ASCII) gets in (D-094).
    private static void ValidateKey(string? key, ValidationErrors errors)
    {
        const string field = nameof(ReserveSeatsCommand.IdempotencyKey);

        if (string.IsNullOrEmpty(key))
        {
            errors.Add(field, "The idempotency key is required.");
        }
        else if (key.Length > MaxKeyLength)
        {
            errors.Add(field, $"The idempotency key must be at most {MaxKeyLength} characters.");
        }
        else if (key.Any(c => c is < '!' or > '~'))
        {
            errors.Add(field, "The idempotency key must be printable ASCII without spaces.");
        }
    }
}
