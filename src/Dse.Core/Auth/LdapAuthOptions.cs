// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Auth;

public sealed class LdapAuthOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 636;
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public string? BindDn { get; set; }
    public string? BindPassword { get; set; }
    public string SearchBase { get; set; } = string.Empty;
    public string GroupsFilter { get; set; } = string.Empty;
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
                TimeSpan.FromSeconds(8)));

        return services
            .AddOptions<LdapAuthOptions>(key)
            .BindConfiguration("Ldap")
            .BindConfiguration($"Ldap:{key}")
            .ValidateOnStart()
            .WithFluentValidator<LdapAuthOptions, LdapAuthOptionsValidator>();
    }
}
