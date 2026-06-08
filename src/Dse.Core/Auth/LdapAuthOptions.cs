// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Dse.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public static class LdapAuthDefaults
{
    // Doubles as the options name AND the configuration section path (Ldap → Ad / Oud).
    public const string Ad = "Ldap:Ad";
    public const string Oud = "Ldap:Oud";
}

[ExcludeFromCodeCoverage]
public sealed class LdapAuthOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 636;
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public string BindDn { get; set; } = string.Empty;
    public string BindPassword { get; set; } = string.Empty;
    public string SearchBase { get; set; } = string.Empty;
    public Func<string, string>? GroupsFilter { get; set; }
    public string GroupsAttribute { get; set; } = string.Empty;
}

[ExcludeFromCodeCoverage]
public static class LdapAuthExtensions
{
    public static IServiceCollection AddLdapAd(this IServiceCollection services)
    {
        services
            .AddNamedFluentOptions<LdapAuthOptions>(LdapAuthDefaults.Ad)
            .PostConfigure<IDseEnvironment>((options, env) =>
            {
                options.Host = options.Host.Or("pncbank.com");

                if (env is IDseLocalEnvironment localEnv)
                {
                    options.BindDn = options.BindDn.Or($"{localEnv.Username}@{options.Host}");
                    options.BindPassword = options.BindPassword.Or(localEnv.Password);
                }

                options.SearchBase = options.SearchBase.Or("DC=pncbank,DC=com");
                options.GroupsAttribute = options.GroupsAttribute.Or("memberOf");
                options.GroupsFilter = uid => $"(&(objectClass=user)(!(objectClass=computer))(sAMAccountName={uid}))";
            });

        services.AddLdapDirectory(LdapAuthDefaults.Ad);

        return services;
    }

    public static IServiceCollection AddLdapOud(this IServiceCollection services)
    {
        services
            .AddNamedFluentOptions<LdapAuthOptions>(LdapAuthDefaults.Oud)
            .PostConfigure<IDseEnvironment>((options, env) =>
            {
                options.Host = options.Host.Or(env is IDseDeploymentEnvironment de
                    ? de.Deployment switch
                    {
                        DeploymentEnvironment.Rnd => "mdsemp-rnd.pncint.net",
                        DeploymentEnvironment.Uat => "mdsemp-uat.pncint.net",
                        DeploymentEnvironment.Qa => "mdsemp-qa.pncint.net",
                        _ => "mdsemp.pncint.net",
                    }
                    : "mdsemp.pncint.net");

                if (env is IDseLocalEnvironment lc)
                {
                    options.BindDn = options.BindDn.Or($"cn={lc.Username},ou=Employees,ou=People,o=pnc");
                    options.BindPassword = options.BindPassword.Or(lc.Password);
                }

                options.SearchBase = options.SearchBase.Or("o=pnc");
                options.GroupsAttribute = options.GroupsAttribute.Or("memberOf");
                options.GroupsFilter = uid => $"(uid={uid})";
            });


        services.AddLdapDirectory(LdapAuthDefaults.Oud);

        return services;
    }

    // One directory: a keyed connector (the authoritative instance), a plain registration so consumers that want
    // every directory can enumerate them, and a readiness health check bound to that same keyed connector. Keyed
    // by the options name, so AD and OUD stay a single line apart.
    private static void AddLdapDirectory(this IServiceCollection services, string key)
    {
        services.AddKeyedSingleton<LdapConnector>(key, (sp, _) => new LdapConnector(key, sp));
        services.AddSingleton<LdapConnector>(sp => sp.GetRequiredKeyedService<LdapConnector>(key));

        // "Ldap:Ad" → "ldap-ad", giving a clean per-check diagnostic route (/health/ldap-ad).
        string name = $"ldap-{key.Split(':')[^1].ToLowerInvariant()}";
        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                name,
                sp => new LdapHealthCheck(
                    sp.GetRequiredKeyedService<LdapConnector>(key),
                    sp.GetRequiredService<ILogger<LdapHealthCheck>>()),
                HealthStatus.Unhealthy,
                ["ready", "ldap"],
                TimeSpan.FromSeconds(8)));
    }
}
