using System.ComponentModel.DataAnnotations;

namespace SeatReservation.Api.Contracts.Requests;

/// <summary><c>POST /shows</c> body (lld §5.2). Content rules live in the logic layer's <c>CreateShowValidator</c>.</summary>
public sealed class CreateShowRequest
{
    public string? Name { get; init; }

    public List<string?>? Seats { get; init; }

    /// <summary>Integer paise. A fraction, a string or a value beyond int64 fails binding with a 400 (D-003).</summary>
    [Required(ErrorMessage = "price_paise is required.")]
    public long? PricePaise { get; init; }

    public int? PerUserLimit { get; init; }

    /// <summary>Input-only alias for <see cref="PerUserLimit"/>, resolved by <c>DtoMapper</c> (D-085).</summary>
    public int? MaxSeatsPerUser { get; init; }
}
