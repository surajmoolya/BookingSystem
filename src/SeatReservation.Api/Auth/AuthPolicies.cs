using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;

namespace SeatReservation.Api.Auth;

public static class AuthPolicies
{
    /// <summary><c>POST /shows</c>: an authenticated user when <c>Shows:RequireAuth</c> is true, anyone otherwise (D-021).</summary>
    public const string CreateShow = "shows:create";

    public static IServiceCollection AddAuthPolicies(this IServiceCollection services)
    {
        services.AddOptions<AuthorizationOptions>().Configure<IOptions<ShowOptions>>((options, shows) =>
            options.AddPolicy(CreateShow, policy =>
            {
                if (shows.Value.RequireAuth)
                {
                    policy.RequireAuthenticatedUser();
                }
                else
                {
                    policy.RequireAssertion(_ => true);
                }
            }));

        return services;
    }
}
