// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text;
using Dse.Shared;
using JasperFx.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public sealed class LdapConnector(string name, IServiceProvider services) : IDisposable
{
    private readonly IMemoryCache _cache = services.GetRequiredService<IMemoryCache>();
    private readonly IHostEnvironment _env = services.GetRequiredService<IHostEnvironment>();

    private readonly ILogger _logger =
        services.GetRequiredService<ILoggerFactory>().CreateLogger($"{name.Capitalize()}{nameof(LdapConnector)}");

    private readonly IOptionsMonitor<LdapAuthOptions> _monitor = services.GetRequiredService<IOptionsMonitor<LdapAuthOptions>>();
    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);
    private LdapConnection? _connection;
    public LdapAuthOptions Options => _monitor.Get(name);
    public string Name => name;
    public bool Bound => _connection?.Bound == true;

    public void Dispose()
    {
        _connection?.Dispose();
        _semaphore.Dispose();
    }

    public async Task<LdapConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring: another caller may have completed connect+bind while we were waiting.
            if (_connection is null)
            {
                LdapConnectionOptions ldapOpts = new LdapConnectionOptions().UseSsl();

                if (!_env.IsProduction())
                {
                    ldapOpts = ldapOpts.ConfigureRemoteCertificateValidationCallback((_, _, _, _) => true);
                }

                _connection = new LdapConnection(ldapOpts);
            }

            _connection.ConnectionTimeout = (int)Options.ConnectionTimeout.TotalMilliseconds;

            await _connection.ConnectAsync(Options.Host, Options.Port, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(Options.BindDn) &&
                !string.IsNullOrWhiteSpace(Options.BindPassword))
            {
                await _connection.BindAsync(Options.BindDn, Options.BindPassword, cancellationToken)
                    .ConfigureAwait(false);
            }

            return _connection;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask AddMembershipsClaims(ClaimsIdentity identity, string uid)
    {
        try
        {
            foreach (string membership in await GetMembershipsAsync(uid))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, membership));
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to enrich claims from LDAP {LdapName} for user {Uid}", Name, uid);
        }
    }

    private ValueTask<IReadOnlySet<string>> GetMembershipsAsync(string uid) =>
        new(_cache.GetOrCreateAsync($"{name}:memberships:{uid}", _ => SearchAdAsync(uid), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
        })!);

    private async Task<IReadOnlySet<string>> SearchAdAsync(string uid)
    {
        LdapConnection conn = await GetConnectionAsync().ConfigureAwait(false);

        string? groupFilter = Options.GroupsFilter.Replace("{0}", EscapeFilter(uid));

        ILdapSearchResults results = await conn.SearchAsync(
                Options.SearchBase,
                LdapConnection.ScopeSub,
                groupFilter,
                [Options.GroupsAttribute],
                typesOnly: false)
            .ConfigureAwait(false);

        HashSet<string> memberships = new(StringComparer.OrdinalIgnoreCase);

        await foreach (LdapEntry entry in results.ConfigureAwait(false))
        {
            LdapAttribute? attr = entry.GetAttributeSet()
                .FirstOrDefault(a => string.Equals(a.Key, Options.GroupsAttribute, StringComparison.OrdinalIgnoreCase))
                .Value;

            if (attr is not null)
            {
                foreach (string val in attr.StringValueArray)
                {
                    DistinguishedName dn = DistinguishedName.Parse(val);
                    if (dn.GetAttributeString("cn") is { } cn)
                    {
                        memberships.Add(cn);
                    }
                }
            }
        }

        return memberships;
    }

    private static string EscapeFilter(string s)
    {
        StringBuilder sb = new(s.Length + 8);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\5c"); break;

                case '(': sb.Append(@"\28"); break;

                case ')': sb.Append(@"\29"); break;

                case '*': sb.Append(@"\2a"); break;

                case '\0': sb.Append(@"\00"); break;

                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }
}
