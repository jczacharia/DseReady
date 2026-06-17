// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Auth;

/// <summary>Binds an LDAP directory used to resolve an authenticated user's group memberships into roles.</summary>
public sealed class LdapAuthOptions
{
    /// <summary>Directory server hostname.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Directory server port (636 = LDAPS).</summary>
    public int Port { get; set; } = 636;

    /// <summary>Time budget for establishing the connection before a lookup is treated as failed.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a user's group memberships are cached — freshness vs. directory load.</summary>
    public TimeSpan MembershipCacheTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Service-account DN for the initial bind; <see langword="null"/> binds anonymously.</summary>
    public string? BindDn { get; set; }

    /// <summary>Password for <see cref="BindDn"/>.</summary>
    public string? BindPassword { get; set; }

    /// <summary>Subtree DN under which the group search is scoped.</summary>
    public string SearchBase { get; set; } = string.Empty;

    /// <summary>Group-search filter; the <c>{0}</c> placeholder is substituted with the username.</summary>
    public string GroupsFilter { get; set; } = string.Empty;

    /// <summary>Attribute on each matched entry whose value becomes a role claim.</summary>
    public string GroupsAttribute { get; set; } = string.Empty;
}

public sealed class LdapAuthOptionsValidator : AbstractValidator<LdapAuthOptions>
{
    public LdapAuthOptionsValidator()
    {
        RuleFor(o => o.Host).NotEmpty();
        RuleFor(o => o.Port).InclusiveBetween(from: 1, to: 65535);
        RuleFor(o => o.ConnectionTimeout).GreaterThan(TimeSpan.Zero);
        RuleFor(o => o.SearchBase).NotEmpty();
        RuleFor(o => o.GroupsFilter)
            .NotEmpty()
            .Must(f => f.Contains("{0}"))
            .WithMessage("Must contain a '{0}' placeholder for the username");
    }
}

public static class LdapAuthExtensions
{
    public static OptionsBuilder<LdapAuthOptions> AddLdapAuth(this IServiceCollection services, string key)
    {
        services.AddKeyedSingleton<LdapConnector>(key, (sp, _) => new LdapConnector(key, sp));
        services.AddSingleton<LdapConnector>(sp => sp.GetRequiredKeyedService<LdapConnector>(key));

        services.AddHealthChecks()
            .Add(new HealthCheckRegistration($"ldap-{key.ToLowerInvariant()}",
                sp => new LdapHealthCheck(
                    sp.GetRequiredKeyedService<LdapConnector>(key),
                    sp.GetRequiredService<ILogger<LdapHealthCheck>>()),
                HealthStatus.Unhealthy,
                ["ready", "ldap"],
                HealthCheckDefaults.ReadinessTimeout));

        return services
            .AddOptions<LdapAuthOptions>(key)
            .BindConfiguration("Ldap")
            .BindConfiguration($"Ldap:{key}")
            .ValidateOnStart()
            .WithFluentValidator<LdapAuthOptions, LdapAuthOptionsValidator>();
    }
}
