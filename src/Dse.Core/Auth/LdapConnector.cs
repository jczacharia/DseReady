// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;

namespace Dse.Auth;

public sealed class LdapConnector(string name, IServiceProvider services) : IDisposable
{
    private readonly IMemoryCache _cache = services.GetRequiredService<IMemoryCache>();
    private readonly DseEnvironment _env = services.GetRequiredService<DseEnvironment>();
    private readonly IOptionsMonitor<LdapAuthOptions> _monitor = services.GetRequiredService<IOptionsMonitor<LdapAuthOptions>>();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private LdapConnection? _connection;
    private LdapAuthOptions Options => _monitor.Get(name);

    public void Dispose()
    {
        _connection?.Dispose();
        _semaphore.Dispose();
    }

    private async Task<LdapConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring: another caller may have completed connect+bind while we were waiting.
            if (_connection is null)
            {
                LdapConnectionOptions ldapOpts = new LdapConnectionOptions().UseSsl();

                if (!_env.IsDeployment)
                {
                    ldapOpts = ldapOpts.ConfigureRemoteCertificateValidationCallback((_, _, _, _) => true);
                }

                _connection = new LdapConnection(ldapOpts);
            }

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

    public ValueTask<IReadOnlySet<string>> GetMembershipsAsync(string uid) =>
        new(_cache.GetOrCreateAsync($"{name}:memberships:{uid}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return SearchAdAsync(uid);
        })!);

    private async Task<IReadOnlySet<string>> SearchAdAsync(string uid)
    {
        LdapConnection conn = await GetConnectionAsync().ConfigureAwait(false);

        ILdapSearchResults results = await conn.SearchAsync(
                Options.SearchBase,
                LdapConnection.ScopeSub,
                Options.GroupsFilter?.Invoke(EscapeFilter(uid)),
                [Options.GroupsAttribute],
                false)
            .ConfigureAwait(false);

        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);

        await foreach (LdapEntry entry in results.ConfigureAwait(false))
        {
            if (entry.GetOrDefault(Options.GroupsAttribute) is { } attr)
            {
                foreach (string dn in attr.StringValueArray)
                {
                    set.Add(dn);
                }
            }

#pragma warning disable S1751 // uid is unique — first matching entry is THE user; rest of the result stream is wasted.
            break;
#pragma warning restore S1751
        }

        return set;
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
