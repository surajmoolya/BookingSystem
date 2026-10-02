using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SeatReservation.Application;
using SeatReservation.Application.Options;

namespace SeatReservation.UnitTests.Options;

public class OptionsValidatorTests
{
    private static readonly ReservationOptionsValidator Reservations = new();
    private static readonly ShowOptionsValidator Shows = new();

    [Fact]
    public void Default_options_are_valid()
    {
        Assert.True(Reservations.Validate(null, new ReservationOptions()).Succeeded);
        Assert.True(Shows.Validate(null, new ShowOptions()).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Per_user_limit_outside_the_schema_range_is_rejected_with_the_config_key(int limit)
    {
        var result = Reservations.Validate(null, new ReservationOptions { DefaultPerUserLimit = limit });

        Assert.True(result.Failed);
        Assert.Contains("Reservations:DefaultPerUserLimit", result.FailureMessage);
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var result = Reservations.Validate(null, new ReservationOptions { DefaultPerUserLimit = 0, MaxSeatsPerRequest = 0 });

        Assert.Equal(2, result.Failures!.Count());
    }

    [Fact]
    public void Non_positive_max_seats_is_rejected()
    {
        var result = Shows.Validate(null, new ShowOptions { MaxSeats = 0 });

        Assert.True(result.Failed);
        Assert.Contains("Shows:MaxSeats", result.FailureMessage);
    }

    [Fact]
    public void AddApplication_registers_validators_so_binding_in_the_host_enforces_them()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddOptions<ReservationOptions>().Configure(o => o.MaxSeatsPerRequest = 0);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ReservationOptions>>().Value);

        Assert.Contains("Reservations:MaxSeatsPerRequest", ex.Message);
    }

    [Fact]
    public void AddApplication_registers_the_system_clock_and_guid_generator()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SystemClock>(provider.GetRequiredService<SeatReservation.Application.Abstractions.IClock>());
        var ids = provider.GetRequiredService<SeatReservation.Application.Abstractions.IIdGenerator>();
        Assert.NotEqual(ids.NewId(), ids.NewId());
    }

    [Fact]
    public void AddApplication_twice_does_not_duplicate_validators()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddApplication();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<ShowOptions>));
    }
}
