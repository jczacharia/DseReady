// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Auth;

public static class LdapAuthDefaults
{
    public const string Ad = "Ldap.Ad";
    public const string Oud = "Ldap.Oud";
}

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

public static class LdapAuthExtensions
{
    public static IServiceCollection AddLdapAd(this IServiceCollection services)
    {
        services
            .AddFluentOptions<LdapAuthOptions>(LdapAuthDefaults.Ad)
            .PostConfigure<DseEnvironment>((options, env) =>
            {
                options.Host = options.Host.IfEmpty("pncbank.com");

                options.BindDn =
                    options.BindDn.IfEmpty(env is DseEnvironment.Dev { Username: { } u } ? $"{u}@{options.Host}" : string.Empty);

                options.BindPassword =
                    options.BindPassword.IfEmpty(env is DseEnvironment.Dev { Password: { } p } ? p : string.Empty);

                options.SearchBase = options.SearchBase.IfEmpty("DC=pncbank,DC=com");
                options.GroupsAttribute = options.GroupsAttribute.IfEmpty("memberOf");
                options.GroupsFilter = uid => $"(&(objectClass=user)(!(objectClass=computer))(sAMAccountName={uid}))";
            });

        services.AddKeyedSingleton<LdapConnector>(LdapAuthDefaults.Oud,
            static (sp, name) => new LdapConnector((string)name, sp));

        services.AddSingleton<LdapConnector>(static sp => sp.GetRequiredKeyedService<LdapConnector>(LdapAuthDefaults.Oud));

        return services;
    }

    public static IServiceCollection AddLdapOud(this IServiceCollection services)
    {
        services
            .AddFluentOptions<LdapAuthOptions>(LdapAuthDefaults.Oud)
            .PostConfigure<DseEnvironment>((options, env) =>
            {
                options.Host = options.Host.IfEmpty(env switch
                {
                    DseEnvironment.Rnd => "mdsemp-rnd.pncint.net",
                    DseEnvironment.Uat => "mdsemp-uat.pncint.net",
                    DseEnvironment.Qa => "mdsemp-qa.pncint.net",
                    _ => "mdsemp.pncint.net",
                });

                options.BindDn =
                    options.BindDn.IfEmpty(env is DseEnvironment.Dev { Username: { } u }
                        ? $"cn={u},ou=Employees,ou=People,o=pnc"
                        : string.Empty);

                options.BindPassword =
                    options.BindPassword.IfEmpty(env is DseEnvironment.Dev { Password: { } p } ? p : string.Empty);

                options.SearchBase = options.SearchBase.IfEmpty("o=pnc");
                options.GroupsAttribute = options.GroupsAttribute.IfEmpty("groupMembership");
                options.GroupsFilter = uid => $"(&(objectClass=user)(!(objectClass=computer))(sAMAccountName={uid}))";
            });


        services.AddKeyedSingleton<LdapConnector>(LdapAuthDefaults.Ad,
            static (sp, name) => new LdapConnector((string)name, sp));

        services.AddSingleton<LdapConnector>(static sp => sp.GetRequiredKeyedService<LdapConnector>(LdapAuthDefaults.Ad));

        return services;
    }
}
