// Copyright (c) PNC Financial Services. All rights reserved.


using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thinktecture;

namespace Dse;

public sealed record LocalCredentials(string Username, string Password);

[Union]
public abstract partial record DseEnv
{
    public const string DeploymentTierEnvVarName = "DEPLOYMENT_ENVIRONMENT";

    public static readonly bool IsReleaseBuild = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyConfigurationAttribute>()
        ?.Configuration == "Release";

    private DseEnv(string name) => Name = name;
    public string Name { get; }

    public bool IsDeployment => this is Rnd or Uat or Qa or Prod;

    public LocalCredentials? LocalCredentials { get; private set; }

    /// <summary>
    ///     Resolves the tier. Local dev and integration tests follow ASPNETCORE_ENVIRONMENT; deployed tiers run as
    ///     "Production" and name themselves via <see cref="DeploymentTierEnvVarName" />. Throws — never guesses a default —
    ///     because a wrong environment identity (writing to the wrong indices, running the wrong schedule) is far
    ///     worse than a pod that refuses to start.
    /// </summary>
    public static DseEnv From(IConfiguration cfg, IHostEnvironment host)
    {
        DseEnv result;

        if (host.IsDevelopment())
        {
            result = new Dev();
        }
        else if (host.EnvironmentName == "Test")
        {
            result = new Test();
        }
        else
        {
            result = cfg[DeploymentTierEnvVarName]?.Trim().ToUpperInvariant() switch
            {
                "RND" => new Rnd(),
                "UAT" => new Uat(),
                "QA" => new Qa(),
                "PROD" => new Prod(),
                var unknown => throw new InvalidOperationException($"Unrecognized {DeploymentTierEnvVarName} value '{unknown}'."),
            };
        }

        if (!IsReleaseBuild)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cfg["Local:Username"], "Missing Local:Username configuration value.");
            ArgumentException.ThrowIfNullOrWhiteSpace(cfg["Local:Password"], "Missing Local:Password configuration value.");
            result.LocalCredentials = new LocalCredentials(cfg["Local:Username"]!, cfg["Local:Password"]!);
        }

        return result;
    }

    public sealed record Dev() : DseEnv("DEV");

    public sealed record Test() : DseEnv("TEST");

    public sealed record Rnd() : DseEnv("RND");

    public sealed record Uat() : DseEnv("UAT");

    public sealed record Qa() : DseEnv("QA");

    public sealed record Prod() : DseEnv("PROD");
}

public static class DseEnvironmentExtensions
{
    /// <summary>
    ///     Resolves and registers <see cref="DseEnv" /> at startup (fail-fast). A misconfigured tier
    ///     throws here in the composition root, with the cause written synchronously to stderr — so a deployed pod
    ///     surfaces it in its logs instead of crash-looping silently the way the old lazy DI factory did.
    /// </summary>
    public static IServiceCollection AddDseEnv(this IServiceCollection services, IConfiguration cfg, IHostEnvironment host)
    {
        DseEnv env;
        try
        {
            env = DseEnv.From(cfg, host);
        }
        catch (Exception ex)
        {
            // Console.Error flushes immediately, so the message survives a crash even when a buffered logger
            // (which only comes up later, after Build) never gets the chance to flush.
            Console.Error.WriteLine($"[FATAL] DseEnvironment resolution failed: {ex.Message}");
            throw;
        }

        return services.AddSingleton(env);
    }
}
