using Microsoft.Extensions.DependencyInjection;

namespace SeatReservation.Application;

public static class DependencyInjection
{
    /// <summary>Registers the logic-layer services. Populated from T-1.2 onwards.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
