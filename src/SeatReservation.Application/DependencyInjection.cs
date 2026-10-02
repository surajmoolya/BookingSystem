using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Auth;
using SeatReservation.Application.Options;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;

namespace SeatReservation.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the logic-layer services and the validators for its options. Binding the options to
    /// configuration (and <c>ValidateOnStart</c>) happens in the composition root, because this layer
    /// may only reference the logging and options abstractions.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<IReservationMetrics, NullReservationMetrics>();

        services.TryAddSingleton<AuthService>();
        services.TryAddSingleton<CancellationService>();
        services.TryAddSingleton<CreateShowValidator>();
        services.TryAddSingleton<ReserveSeatsValidator>();
        services.TryAddSingleton<ReservationService>();
        services.TryAddSingleton<ShowCatalog>();
        services.TryAddSingleton<ShowService>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ReservationOptions>, ReservationOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ShowOptions>, ShowOptionsValidator>());

        return services;
    }
}
