// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thinktecture;

namespace Dse;

[Union]
public abstract partial record DseEnvironment
{
    public const string DeploymentTierEnvVarName = "DEPLOYMENT_ENVIRONMENT";
    private DseEnvironment(string name) => Name = name;
    public string Name { get; }

    public bool IsDeployment => this is Rnd or Uat or Qa or Prod;

    /// <summary>
    ///     Resolves the tier. Local dev and integration tests follow ASPNETCORE_ENVIRONMENT; deployed tiers run as
    ///     "Production" and name themselves via <see cref="DeploymentTierEnvVarName" />. Throws — never guesses a default —
    ///     because a wrong environment identity (writing to the wrong indices, running the wrong schedule) is far
    ///     worse than a pod that refuses to start.
    /// </summary>
    public static DseEnvironment From(IConfiguration cfg, IHostEnvironment host)
    {
        if (host.IsDevelopment())
        {
            return new Dev(
                cfg["Dev:Username"] ?? throw new InvalidOperationException("Missing Dev:Username configuration value."),
                cfg["Dev:Password"] ?? throw new InvalidOperationException("Missing Dev:Password configuration value."));
        }

        if (host.EnvironmentName == "Test")
        {
            return new Test();
        }

        return cfg[DeploymentTierEnvVarName]?.Trim().ToUpperInvariant() switch
        {
            "RND" => new Rnd(),
            "UAT" => new Uat(),
            "QA" => new Qa(),
            "PROD" => new Prod(),
            var unknown => throw new InvalidOperationException($"Unrecognized {DeploymentTierEnvVarName} value '{unknown}'."),
        };
    }

    public sealed record Dev(string Username, string Password) : DseEnvironment("DEV");

    public sealed record Test() : DseEnvironment("TEST");

    public sealed record Rnd() : DseEnvironment("RND");

    public sealed record Uat() : DseEnvironment("UAT");

    public sealed record Qa() : DseEnvironment("QA");

    public sealed record Prod() : DseEnvironment("PROD");
}

public static class DseEnvironmentExtensions
{
    /// <summary>
    ///     Resolves and registers <see cref="DseEnvironment" /> at startup (fail-fast). A misconfigured tier
    ///     throws here in the composition root, with the cause written synchronously to stderr — so a deployed pod
    ///     surfaces it in its logs instead of crash-looping silently the way the old lazy DI factory did.
    /// </summary>
    public static IServiceCollection AddDseEnvironment(
        this IServiceCollection services,
        IConfiguration cfg,
        IHostEnvironment host)
    {
        DseEnvironment environment;
        try
        {
            environment = DseEnvironment.From(cfg, host);
        }
        catch (Exception ex)
        {
            // Console.Error flushes immediately, so the message survives a crash even when a buffered logger
            // (which only comes up later, after Build) never gets the chance to flush.
            Console.Error.WriteLine($"[FATAL] DseEnvironment resolution failed: {ex.Message}");
            throw;
        }

        return services.AddSingleton(environment);
    }
}
