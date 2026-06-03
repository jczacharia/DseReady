// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Dse.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public static class LdapAuthDefaults
{
    public const string Ad = "Ldap.Ad";
    public const string Oud = "Ldap.Oud";
}

[ExcludeFromCodeCoverage]
public sealed class LdapAuthOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 636;
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
            .AddFluentOptions<LdapAuthOptions>(LdapAuthDefaults.Ad)
            .PostConfigure<DseEnv>((options, env) =>
            {
                options.Host = options.Host.Or("pncbank.com");

                if (env is { LocalCredentials: { } lc })
                {
                    options.BindDn = options.BindDn.Or($"{lc.Username}@{options.Host}");
                    options.BindPassword = options.BindPassword.Or(lc.Password);
                }

                options.SearchBase = options.SearchBase.Or("DC=pncbank,DC=com");
                options.GroupsAttribute = options.GroupsAttribute.Or("memberOf");
                options.GroupsFilter = uid => $"(&(objectClass=user)(!(objectClass=computer))(sAMAccountName={uid}))";
            });

        services.AddKeyedSingleton<LdapConnector>(LdapAuthDefaults.Ad, (sp, _) => new LdapConnector(LdapAuthDefaults.Ad, sp));
        services.AddSingleton<LdapConnector>(static sp => sp.GetRequiredKeyedService<LdapConnector>(LdapAuthDefaults.Ad));

        return services;
    }

    public static IServiceCollection AddLdapOud(this IServiceCollection services)
    {
        services
            .AddFluentOptions<LdapAuthOptions>(LdapAuthDefaults.Oud)
            .PostConfigure<DseEnv>((options, env) =>
            {
                options.Host = options.Host.Or(env switch
                {
                    DseEnv.Rnd => "mdsemp-rnd.pncint.net",
                    DseEnv.Uat => "mdsemp-uat.pncint.net",
                    DseEnv.Qa => "mdsemp-qa.pncint.net",
                    _ => "mdsemp.pncint.net",
                });

                if (env is { LocalCredentials: { } lc })
                {
                    options.BindDn = options.BindDn.Or($"cn={lc.Username},ou=Employees,ou=People,o=pnc");
                    options.BindPassword = options.BindPassword.Or(lc.Password);
                }

                options.SearchBase = options.SearchBase.Or("o=pnc");
                options.GroupsAttribute = options.GroupsAttribute.Or("groupMembership");
                options.GroupsFilter = uid => $"(&(objectClass=user)(!(objectClass=computer))(sAMAccountName={uid}))";
            });


        services.AddKeyedSingleton<LdapConnector>(LdapAuthDefaults.Oud, (sp, _) => new LdapConnector(LdapAuthDefaults.Oud, sp));
        services.AddSingleton<LdapConnector>(static sp => sp.GetRequiredKeyedService<LdapConnector>(LdapAuthDefaults.Oud));

        return services;
    }
}
