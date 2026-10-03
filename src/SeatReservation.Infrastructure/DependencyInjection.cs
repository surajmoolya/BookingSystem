using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Infrastructure.Migrations;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Repositories;
using SeatReservation.Infrastructure.Transactions;

namespace SeatReservation.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the repository layer. The remaining repositories arrive in T-3.6.</summary>
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

        // Needs an IMetricFactory from the composition root (the host's Prometheus registry, D-097).
        services.TryAddSingleton<DbMetrics>();
        services.TryAddSingleton<PgQueryExecutor>();
        services.TryAddSingleton<ITransactionRunner, PgTransactionRunner>();
        services.TryAddSingleton<IShowReadRepository, ShowReadRepository>();
        services.TryAddSingleton<IReservationReadRepository, ReservationReadRepository>();
        services.TryAddSingleton<ISeatStatisticsQuery, SeatStatisticsQuery>();

        services.TryAddSingleton<MigrationState>();
        services.TryAddSingleton<IReadinessState>(sp => sp.GetRequiredService<MigrationState>());
        services.AddHostedService<MigrationRunner>();

        return services;
    }
}
