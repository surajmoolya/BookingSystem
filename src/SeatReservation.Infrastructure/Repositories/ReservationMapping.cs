using Npgsql;
using NpgsqlTypes;
using SeatReservation.Application.Reservations;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>The <c>reservations</c> columns every reservation read selects, how a row becomes a <see cref="Reservation"/>, and typed parameters.</summary>
public static class ReservationMapping
{
    public const string Columns =
        "id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status, created_at, cancelled_at";

    public const string ByKeySql = $"SELECT {Columns} FROM reservations WHERE user_id = $1 AND idempotency_key = $2";

    public const string ByIdSql = $"SELECT {Columns} FROM reservations WHERE id = $1";

    public static Reservation Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetFieldValue<byte[]>(4),
        reader.GetFieldValue<string[]>(5),
        reader.GetInt64(6),
        ParseStatus(reader.GetString(7)),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));

    /// <summary>Reads at most one row.</summary>
    public static async Task<Reservation?> ReadSingleAsync(NpgsqlDataReader reader, CancellationToken ct) =>
        await reader.ReadAsync(ct) ? Read(reader) : null;

    public static string ToText(ReservationStatus status) => status switch
    {
        ReservationStatus.Confirmed => "confirmed",
        ReservationStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static ReservationStatus ParseStatus(string value) => value switch
    {
        "confirmed" => ReservationStatus.Confirmed,
        "cancelled" => ReservationStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unknown reservation status '{value}' in the database."),
    };

    public static NpgsqlParameter Text(string value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Text };

    public static NpgsqlParameter Uuid(Guid value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Uuid };

    public static NpgsqlParameter TextArray(IReadOnlyList<string> values) =>
        new() { Value = values as string[] ?? values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    /// <summary>Npgsql only writes a UTC (offset 0) <see cref="DateTimeOffset"/> to <c>timestamptz</c>.</summary>
    public static NpgsqlParameter Timestamp(DateTimeOffset value) =>
        new() { Value = value.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz };
}
