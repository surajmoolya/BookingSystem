using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SeatReservation.Api.Errors;
using SeatReservation.Api.Options;
using SeatReservation.Application.Abstractions;

namespace SeatReservation.Api.Auth;

public static class JwtSetup
{
    /// <summary>Small allowance for clock drift between issuer and validator; the default 5 minutes would keep expired tokens alive.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// JWT issuing (<see cref="JwtTokenIssuer"/>) and bearer validation, both from <see cref="AuthOptions"/> and the same
    /// <see cref="SigningKeyProvider"/> key. A missing or invalid token on an <c>[Authorize]</c> endpoint is a 401 problem.
    /// </summary>
    public static IServiceCollection AddJwtAuth(this IServiceCollection services)
    {
        services.AddSingleton<SigningKeyProvider>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>, SigningKeyProvider>((bearer, auth, key) => Configure(bearer, auth.Value, key));
        services.AddAuthorization();
        services.AddAuthPolicies();

        return services;
    }

    private static void Configure(JwtBearerOptions bearer, AuthOptions auth, SigningKeyProvider key)
    {
        bearer.MapInboundClaims = false;   // keep "sub" as "sub" (ClaimsPrincipalExtensions.GetUserId)
        bearer.IncludeErrorDetails = false;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = auth.Issuer,
            ValidateAudience = true,
            ValidAudience = auth.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = ClockSkew,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key.Key,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
        bearer.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.Headers.WWWAuthenticate = context.AuthenticateFailure is null ? "Bearer" : "Bearer error=\"invalid_token\"";
                var problem = ProblemFactory.Create(context.HttpContext, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized);
                await ProblemFactory.WriteAsync(context.HttpContext, problem, context.HttpContext.RequestAborted);
            },
        };
    }
}
