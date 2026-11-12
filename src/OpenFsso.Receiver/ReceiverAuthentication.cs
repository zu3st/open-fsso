using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenFsso.Core;

namespace OpenFsso.Receiver;

// Every endpoint requires a Kerberos-authenticated Windows user unless it opts
// into the health key.
public static class ReceiverAuthentication
{
    public const string Windows = "Windows", HealthKey = "HealthKey";

    public static IServiceCollection AddReceiverAuthentication(this IServiceCollection services, string healthApiKey)
    {
        services.AddSingleton(sp => new RejectionLog(sp.GetRequiredService<ILoggerFactory>().CreateLogger("OpenFsso.Receiver.Rejections"), TimeProvider.System));
        services.AddAuthentication(Windows)
            .AddScheme<AuthenticationSchemeOptions, WindowsHandler>(Windows, null)
            .AddScheme<HealthKeyOptions, HealthKeyHandler>(HealthKey, options => options.Digest = HealthKeyHandler.Digest(healthApiKey));
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(Windows).RequireClaim(ClaimTypes.PrimarySid).Build())
            .AddPolicy(HealthKey, policy => policy.AddAuthenticationSchemes(HealthKey).RequireAuthenticatedUser());
        return services;
    }

    public static TBuilder RequireHealthKey<TBuilder>(this TBuilder endpoint) where TBuilder : IEndpointConventionBuilder => endpoint.RequireAuthorization(HealthKey);
}

// HTTP.sys authenticates natively on the URL group; this exposes that identity.
// The framework's own HTTP.sys scheme stays disabled because it would add a
// Kerberos challenge to every 401, including the Bearer-only health route.
// The framework logs every challenge and failure, which anonymous requests
// could use to flood the log, so the handlers log through RejectionLog only.
internal sealed class WindowsHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, UrlEncoder encoder, RejectionLog rejections) : AuthenticationHandler<AuthenticationSchemeOptions>(options, NullLoggerFactory.Instance, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.User.Identity is WindowsIdentity { IsAuthenticated: true } identity)
        {
            // Only a user's Kerberos ticket may create or renew a mapping: no NTLM,
            // machine account, or name that could break the syslog line.
            if (identity.User == null || !string.Equals(identity.AuthenticationType, "Kerberos", StringComparison.OrdinalIgnoreCase) || identity.Name.EndsWith('$') || identity.Name.Any(c => char.IsControl(c) || c is '"' or '<' or '>'))
            {
                rejections.Identity(identity.AuthenticationType);
                return Task.FromResult(Success(new ClaimsIdentity([new Claim(ClaimTypes.Name, identity.Name)], Scheme.Name)));
            }

            return Task.FromResult(Success(new AuthenticatedUser(identity.User.Value, identity.Name)));
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }

    private AuthenticateResult Success(AuthenticatedUser user) =>
        Success(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.PrimarySid, user.Sid)], Scheme.Name));

    private AuthenticateResult Success(ClaimsIdentity identity) => AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name));

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Kerberos";
        return Task.CompletedTask;
    }
}

public sealed class HealthKeyOptions : AuthenticationSchemeOptions
{
    public byte[] Digest { get; set; } = [];
}

internal sealed class HealthKeyHandler(IOptionsMonitor<HealthKeyOptions> options, UrlEncoder encoder, RejectionLog rejections) : AuthenticationHandler<HealthKeyOptions>(options, NullLoggerFactory.Instance, encoder)
{
    // Equal-length digests make the comparison constant time for any key length.
    public static byte[] Digest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Headers.Authorization;
        if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var header) || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || header.Parameter == null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (CryptographicOperations.FixedTimeEquals(Options.Digest, Digest(header.Parameter)))
        {
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(Scheme.Name)), Scheme.Name)));
        }

        rejections.HealthKey();
        return Task.FromResult(AuthenticateResult.Fail("Invalid health key."));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer";
        Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }
}
