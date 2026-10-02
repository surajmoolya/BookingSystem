using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the repository layer. Runner, repositories and migrations arrive in T-1.6 onwards.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();

        // Built lazily on first use so configuration supplied late (tests, env) is honoured; the container disposes both pools.
        services.TryAddSingleton(sp => DataSources.Create(
            ConnectionStringResolver.Resolve(sp.GetRequiredService<IConfiguration>()),
            sp.GetRequiredService<IOptions<DatabaseOptions>>().Value,
            includeErrorDetail: sp.GetRequiredService<IHostEnvironment>().IsDevelopment()));

        return services;
    }
}
