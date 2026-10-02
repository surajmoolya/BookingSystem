using System.Reflection;
using Npgsql;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Persistence;

/// <summary>Applies the embedded V001 script to a fresh database and checks that the schema enforces its own invariants.</summary>
[Collection(PostgresCollection.Name)]
public class SchemaTests(PostgresFixture postgres)
{
    private static readonly Guid ShowId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public void V001_is_embedded_in_the_Infrastructure_assembly()
    {
        var names = typeof(DataSources).Assembly.GetManifestResourceNames();

        Assert.Contains("SeatReservation.Infrastructure.Migrations.Scripts.V001__init.sql", names);
    }

    [Fact]
    public async Task Script_creates_the_tables_and_indexes()
    {
        await using var conn = await FreshSchemaAsync();

        var tables = await ColumnAsync(conn, "SELECT tablename FROM pg_tables WHERE schemaname='public'");
        var indexes = await ColumnAsync(conn, "SELECT indexname FROM pg_indexes WHERE schemaname='public'");

        Assert.Equivalent(new[] { "schema_migrations", "shows", "reservations", "seats" }, tables);
        foreach (var expected in new[]
                 {
                     "ix_reservations_show_user", "ix_seats_show_user", "ix_seats_reservation",
                     "ux_seats_show_ordinal", "ix_shows_created", "uq_reservations_user_key", "pk_seats",
                 })
        {
            Assert.Contains(expected, indexes);
        }
    }

    [Fact]
    public async Task Show_per_user_limit_defaults_to_4_and_is_bounded()
    {
        await using var conn = await FreshSchemaAsync();
        await ExecAsync(conn, $"INSERT INTO shows (id, name, price_paise, total_seats) VALUES ('{ShowId}', 'x', 100, 3)");

        Assert.Equal(4, await ScalarAsync<int>(conn, "SELECT per_user_limit FROM shows"));
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            "INSERT INTO shows (id, name, price_paise, per_user_limit, total_seats) VALUES (gen_random_uuid(), 'x', 1, 0, 1)");
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            "INSERT INTO shows (id, name, price_paise, per_user_limit, total_seats) VALUES (gen_random_uuid(), 'x', 1, 101, 1)");
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            "INSERT INTO shows (id, name, price_paise, total_seats) VALUES (gen_random_uuid(), 'x', -1, 1)");
    }

    [Fact]
    public async Task Seat_owner_check_ties_status_to_user_and_reservation()
    {
        await using var conn = await FreshSchemaAsync();
        await SeedShowWithSeatAsync(conn);

        // available seat with an owner, and confirmed seat without one, are both illegal
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            $"UPDATE seats SET user_id = 'alice' WHERE show_id = '{ShowId}' AND label = 'A1'");
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            $"UPDATE seats SET status = 'confirmed' WHERE show_id = '{ShowId}' AND label = 'A1'");
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            $"UPDATE seats SET status = 'sold' WHERE show_id = '{ShowId}' AND label = 'A1'");
    }

    [Fact]
    public async Task Confirming_a_seat_for_a_real_reservation_is_allowed_and_a_dangling_reservation_is_not()
    {
        await using var conn = await FreshSchemaAsync();
        await SeedShowWithSeatAsync(conn);
        var reservationId = Guid.NewGuid();
        await InsertReservationAsync(conn, reservationId, "alice", "k1");

        await ExecAsync(conn, $"UPDATE seats SET status='confirmed', user_id='alice', reservation_id='{reservationId}' WHERE show_id='{ShowId}' AND label='A1'");

        await AssertViolationAsync(conn, PostgresErrorCodes.ForeignKeyViolation,
            $"UPDATE seats SET reservation_id = gen_random_uuid() WHERE show_id='{ShowId}' AND label='A1'");
    }

    [Fact]
    public async Task Idempotency_key_is_unique_per_user_not_globally()
    {
        await using var conn = await FreshSchemaAsync();
        await SeedShowWithSeatAsync(conn);
        await InsertReservationAsync(conn, Guid.NewGuid(), "alice", "same-key");

        await InsertReservationAsync(conn, Guid.NewGuid(), "bob", "same-key");   // another user: fine
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertReservationAsync(conn, Guid.NewGuid(), "alice", "same-key"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("uq_reservations_user_key", ex.ConstraintName);
    }

    [Fact]
    public async Task Cancelled_status_and_cancelled_at_must_agree()
    {
        await using var conn = await FreshSchemaAsync();
        await SeedShowWithSeatAsync(conn);
        var id = Guid.NewGuid();
        await InsertReservationAsync(conn, id, "alice", "k1");

        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation, $"UPDATE reservations SET status='cancelled' WHERE id='{id}'");
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation, $"UPDATE reservations SET cancelled_at=now() WHERE id='{id}'");
        await ExecAsync(conn, $"UPDATE reservations SET status='cancelled', cancelled_at=now() WHERE id='{id}'");
    }

    [Fact]
    public async Task Reservation_field_bounds_are_enforced()
    {
        await using var conn = await FreshSchemaAsync();
        await SeedShowWithSeatAsync(conn);

        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            $"INSERT INTO reservations (id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status) VALUES (gen_random_uuid(), '{ShowId}', 'a', '', '\\x00', ARRAY['A1'], 1, 'confirmed')");
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            $"INSERT INTO reservations (id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status) VALUES (gen_random_uuid(), '{ShowId}', 'a', '{new string('k', 129)}', '\\x00', ARRAY['A1'], 1, 'confirmed')");
        await AssertViolationAsync(conn, PostgresErrorCodes.CheckViolation,
            $"INSERT INTO reservations (id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status) VALUES (gen_random_uuid(), '{ShowId}', 'a', 'k', '\\x00', ARRAY['A1'], -5, 'confirmed')");
    }

    [Fact]
    public async Task Seat_labels_and_ordinals_are_unique_within_a_show()
    {
        await using var conn = await FreshSchemaAsync();
        await SeedShowWithSeatAsync(conn);

        await AssertViolationAsync(conn, PostgresErrorCodes.UniqueViolation,
            $"INSERT INTO seats (show_id, label, ordinal) VALUES ('{ShowId}', 'A1', 2)");      // duplicate label
        await AssertViolationAsync(conn, PostgresErrorCodes.UniqueViolation,
            $"INSERT INTO seats (show_id, label, ordinal) VALUES ('{ShowId}', 'A2', 1)");      // duplicate ordinal
    }

    // ---- helpers ----

    private async Task<NpgsqlConnection> FreshSchemaAsync()
    {
        var conn = new NpgsqlConnection(await postgres.CreateDatabaseAsync());
        await conn.OpenAsync();
        await using var stream = typeof(DataSources).Assembly
            .GetManifestResourceStream("SeatReservation.Infrastructure.Migrations.Scripts.V001__init.sql")!;
        using var reader = new StreamReader(stream);
        await ExecAsync(conn, await reader.ReadToEndAsync());
        return conn;
    }

    private static Task SeedShowWithSeatAsync(NpgsqlConnection conn) =>
        ExecAsync(conn, $"""
            INSERT INTO shows (id, name, price_paise, total_seats) VALUES ('{ShowId}', 'x', 100, 1);
            INSERT INTO seats (show_id, label, ordinal) VALUES ('{ShowId}', 'A1', 1);
            """);

    private static Task InsertReservationAsync(NpgsqlConnection conn, Guid id, string user, string key) =>
        ExecAsync(conn, $"""
            INSERT INTO reservations (id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status)
            VALUES ('{id}', '{ShowId}', '{user}', '{key}', '\x00', ARRAY['A1'], 100, 'confirmed')
            """);

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> ColumnAsync(NpgsqlConnection conn, string sql)
    {
        var values = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task AssertViolationAsync(NpgsqlConnection conn, string expectedSqlState, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, sql));
        Assert.Equal(expectedSqlState, ex.SqlState);
    }
}
