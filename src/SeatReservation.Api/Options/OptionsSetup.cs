using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;

namespace SeatReservation.Api.Options;

public static class OptionsSetup
{
    /// <summary>
    /// Binds the Api-owned options and the Application-owned ones (that layer can't bind configuration itself),
    /// each with <c>ValidateOnStart</c>, so a bad value stops the host at startup naming the offending key.
    /// Infrastructure binds its own <c>DatabaseOptions</c> in <c>AddInfrastructure</c>.
    /// </summary>
    public static IServiceCollection AddValidatedOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidated<AuthOptions, AuthOptionsValidator>(configuration, AuthOptions.SectionName);
        services.AddValidated<AdmissionOptions, AdmissionOptionsValidator>(configuration, AdmissionOptions.SectionName);
        services.AddValidated<MetricsOptions, MetricsOptionsValidator>(configuration, MetricsOptions.SectionName);

        // Validators for these two come from AddApplication().
        services.AddOptions<ReservationOptions>().Bind(configuration.GetSection(ReservationOptions.SectionName)).ValidateOnStart();
        services.AddOptions<ShowOptions>().Bind(configuration.GetSection(ShowOptions.SectionName)).ValidateOnStart();

        return services;
    }

    private static void AddValidated<TOptions, TValidator>(this IServiceCollection services, IConfiguration configuration, string section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, TValidator>());
        services.AddOptions<TOptions>().Bind(configuration.GetSection(section)).ValidateOnStart();
    }
}
