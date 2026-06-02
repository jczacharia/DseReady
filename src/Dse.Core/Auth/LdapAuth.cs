// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dse.Auth;

public static class LdapDefaults
{
    public const string Ad = "Ldap.Ad";
    public const string Oud = "Ldap.Oud";
}

public sealed class LdapAuthOptions : AuthenticationSchemeOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 636;
    public string BindDn { get; set; } = string.Empty;
    public string BindPassword { get; set; } = string.Empty;
    public string SearchBase { get; set; } = string.Empty;
    public Func<string, string>? GroupsFilter { get; set; }
    public string GroupsAttribute { get; set; } = string.Empty;
}

internal class ConfigureAdLdapOptions(DseEnvironment env, IConfiguration config) : IConfigureNamedOptions<LdapAuthOptions>
{
    public void Configure(string? name, LdapAuthOptions options)
    {
        if (name != LdapDefaults.Ad)
        {
            return;
        }

        options.Host = "pncbank.com";

        options.BindDn = config["Ldap:Ad:BindDn"]
                         ?? (env is DseEnvironment.Dev { Username: { } u } ? $"{u}@{options.Host}" : string.Empty);

        options.BindPassword = config["Ldap:Ad:BindPassword"]
                               ?? (env is DseEnvironment.Dev { Password: { } p } ? p : string.Empty);

        options.SearchBase = "DC=pncbank,DC=com";
        options.GroupsFilter = uid => $"(&(objectClass=user)(!(objectClass=computer))(sAMAccountName={uid}))";
        options.GroupsAttribute = "memberOf";
    }

    public void Configure(LdapAuthOptions options) => Configure(LdapDefaults.Ad, options);
}

internal class ConfigureOudLdapOptions(DseEnvironment env, IConfiguration config) : IConfigureNamedOptions<LdapAuthOptions>
{
    public void Configure(string? name, LdapAuthOptions options)
    {
        if (name != LdapDefaults.Oud)
        {
            return;
        }

        options.Host = env switch
        {
            DseEnvironment.Rnd => "mdsemp-rnd.pncint.net",
            DseEnvironment.Uat => "mdsemp-uat.pncint.net",
            DseEnvironment.Qa => "mdsemp-qa.pncint.net",
            _ => "mdsemp.pncint.net",
        };

        options.BindDn = config["Ldap:Oud:BindDn"]
                         ?? (env is DseEnvironment.Dev { Username: { } u }
                             ? $"cn={u},ou=Employees,ou=People,o=pnc"
                             : string.Empty);

        options.BindPassword = config["Ldap:Oud:BindPassword"]
                               ?? (env is DseEnvironment.Dev { Password: { } p } ? p : string.Empty);

        options.SearchBase = "o=pnc";
        options.GroupsFilter = uid => $"(&(objectclass=groupOfNames)(member=cn={uid},ou=Employees,ou=People,o=pnc))";
        options.GroupsAttribute = "groupMembership";
    }

    public void Configure(LdapAuthOptions options) => Configure(LdapDefaults.Oud, options);
}

public static class LdapAuthExtensions
{
    public static AuthenticationBuilder AddLdapAuth(this AuthenticationBuilder builder)
    {
        builder.AddScheme<LdapAuthOptions, LdapAuthHandler>(LdapDefaults.Ad, null);
        builder.AddScheme<LdapAuthOptions, LdapAuthHandler>(LdapDefaults.Oud, null);

        builder.Services.ConfigureOptions<ConfigureAdLdapOptions>();
        builder.Services.ConfigureOptions<ConfigureOudLdapOptions>();

        builder.Services.AddKeyedSingleton<LdapConnector>(LdapDefaults.Ad,
            static (sp, name) => new LdapConnector((string)name, sp));

        builder.Services.AddKeyedSingleton<LdapConnector>(LdapDefaults.Oud,
            static (sp, name) => new LdapConnector((string)name, sp));

        return builder;
    }
}
