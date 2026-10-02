using Npgsql;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.IntegrationTests.Persistence;

public class DatabaseUrlParserTests
{
    [Fact]
    public void Parses_a_full_url_into_its_parts()
    {
        var b = DatabaseUrlParser.Parse("postgres://seatres:secret@db.internal:6543/seatres_prod");

        Assert.Equal("db.internal", b.Host);
        Assert.Equal(6543, b.Port);
        Assert.Equal("seatres_prod", b.Database);
        Assert.Equal("seatres", b.Username);
        Assert.Equal("secret", b.Password);
    }

    [Theory]
    [InlineData("postgres://u:p@host/db")]
    [InlineData("postgresql://u:p@host/db")]
    [InlineData("POSTGRES://u:p@host/db")]
    public void Port_defaults_to_5432_and_both_schemes_are_accepted(string url)
    {
        var b = DatabaseUrlParser.Parse(url);

        Assert.Equal(5432, b.Port);
        Assert.Equal("host", b.Host);
    }

    [Fact]
    public void User_and_password_are_url_decoded()
    {
        var b = DatabaseUrlParser.Parse("postgres://app%40corp:p%40ss%3Aw%2Frd%23%25@host/db");

        Assert.Equal("app@corp", b.Username);
        Assert.Equal("p@ss:w/rd#%", b.Password);
    }

    [Fact]
    public void An_encoded_colon_in_the_user_does_not_split_the_credentials()
    {
        var b = DatabaseUrlParser.Parse("postgres://us%3Aer:pw@host/db");

        Assert.Equal("us:er", b.Username);
        Assert.Equal("pw", b.Password);
    }

    [Fact]
    public void Url_without_a_password_still_parses()
    {
        var b = DatabaseUrlParser.Parse("postgres://onlyuser@host/db");

        Assert.Equal("onlyuser", b.Username);
        Assert.True(string.IsNullOrEmpty(b.Password));
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("allow", SslMode.Allow)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    [InlineData("REQUIRE", SslMode.Require)]
    public void Sslmode_query_parameter_maps_to_SslMode(string value, SslMode expected)
    {
        var b = DatabaseUrlParser.Parse($"postgres://u:p@host/db?sslmode={value}");

        Assert.Equal(expected, b.SslMode);
    }

    [Fact]
    public void Without_sslmode_the_Npgsql_default_is_kept()
    {
        var b = DatabaseUrlParser.Parse("postgres://u:p@host/db");

        Assert.Equal(new NpgsqlConnectionStringBuilder().SslMode, b.SslMode);
    }

    [Fact]
    public void Other_query_parameters_are_ignored_and_sslmode_is_found_among_them()
    {
        var b = DatabaseUrlParser.Parse("postgres://u:p@host/db?application_name=x&sslmode=require&connect_timeout=5");

        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Render_style_internal_url_parses()
    {
        var b = DatabaseUrlParser.Parse("postgresql://seatres_user:Zk3pQ9xLw2Vt8RnB@dpg-d1a2b3c4d5e6f7g8h9i0-a/seatres_x1y2");

        Assert.Equal("dpg-d1a2b3c4d5e6f7g8h9i0-a", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("seatres_x1y2", b.Database);
        Assert.Equal("seatres_user", b.Username);
        Assert.Equal("Zk3pQ9xLw2Vt8RnB", b.Password);
    }

    [Fact]
    public void Render_style_external_url_with_sslmode_parses()
    {
        var b = DatabaseUrlParser.Parse("postgresql://seatres_user:pw@dpg-d1a2b3c4d5e6f7g8h9i0-a.oregon-postgres.render.com/seatres_x1y2?sslmode=require");

        Assert.Equal("dpg-d1a2b3c4d5e6f7g8h9i0-a.oregon-postgres.render.com", b.Host);
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Ipv6_literal_host_is_unbracketed()
    {
        var b = DatabaseUrlParser.Parse("postgres://u:p@[::1]:5433/db");

        Assert.Equal("::1", b.Host);
        Assert.Equal(5433, b.Port);
    }

    [Fact]
    public void ToConnectionString_round_trips_through_Npgsql()
    {
        var text = DatabaseUrlParser.ToConnectionString("postgres://u:p%40w@host:5433/db?sslmode=disable");
        var b = new NpgsqlConnectionStringBuilder(text);

        Assert.Equal("host", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("db", b.Database);
        Assert.Equal("u", b.Username);
        Assert.Equal("p@w", b.Password);
        Assert.Equal(SslMode.Disable, b.SslMode);
    }

    [Theory]
    [InlineData("Host=localhost;Database=x")]
    [InlineData("mysql://u:p@host/db")]
    [InlineData("http://u:p@host/db")]
    [InlineData("")]
    public void Non_postgres_urls_are_rejected(string value)
    {
        Assert.Throws<FormatException>(() => DatabaseUrlParser.Parse(value));
    }

    [Theory]
    [InlineData("postgres://u:p@/db")]
    [InlineData("postgres://u:p@host")]
    [InlineData("postgres://u:p@host/")]
    [InlineData("postgres://u:p@host/db?sslmode=bogus")]
    public void Missing_parts_and_unknown_sslmode_are_rejected(string value)
    {
        Assert.Throws<FormatException>(() => DatabaseUrlParser.Parse(value));
    }

    [Fact]
    public void Error_messages_never_leak_the_password()
    {
        var ex = Assert.Throws<FormatException>(() => DatabaseUrlParser.Parse("postgres://u:SuperSecret@host/db?sslmode=bogus"));

        Assert.DoesNotContain("SuperSecret", ex.Message);
    }

    [Theory]
    [InlineData("postgres://u:p@host/db", true)]
    [InlineData("postgresql://u:p@host/db", true)]
    [InlineData("Host=localhost;Database=seatres", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDatabaseUrl_tells_urls_from_key_value_strings(string? value, bool expected)
    {
        Assert.Equal(expected, DatabaseUrlParser.IsDatabaseUrl(value));
    }
}
