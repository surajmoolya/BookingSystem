using System.Security.Cryptography;
using System.Text;

namespace SeatReservation.Application.Reservations;

/// <summary>
/// Fingerprint of a reserve request, stored with the reservation and compared on key reuse (lld §6.1, D-013).
/// Seat order does not matter; show id and label case do. The <c>v1</c> prefix lets the format change without
/// silently matching old hashes.
/// </summary>
public static class RequestHasher
{
    public const string Version = "v1";

    /// <summary>Duplicate labels must already have been rejected by <see cref="ReserveSeatsValidator"/>.</summary>
    public static byte[] Compute(Guid showId, IEnumerable<string> seats) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(showId, seats)));

    public static string Canonicalize(Guid showId, IEnumerable<string> seats) =>
        $"{Version}|{showId:N}|{string.Join(',', seats.Order(StringComparer.Ordinal))}";
}
