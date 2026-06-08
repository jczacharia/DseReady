// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Dse.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Dse;

public interface IDseEnvironment : IHostEnvironment
{
    public static readonly bool IsRelease = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyConfigurationAttribute>()
        ?.Configuration == "Release";
}

public enum DeploymentEnvironment
{
    Rnd,
    Uat,
    Qa,
    Prod,
}

public interface IDseDeploymentEnvironment : IDseEnvironment
{
    public DeploymentEnvironment Deployment { get; }
}

public interface IDseLocalEnvironment : IDseEnvironment
{
    public string Username { get; init; }
    public string Password { get; init; }
    public string[] Roles { get; init; }
}

[ExcludeFromCodeCoverage]
internal static class DseEnvironmentExtensions
{
    private const string DeploymentEnvVar = "DEPLOYMENT_ENVIRONMENT";

    public static void AddDseEnvironment(this IServiceCollection services) =>
        services.AddSingleton<IDseEnvironment>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var hostEnv = sp.GetRequiredService<IHostEnvironment>();

            if (!IDseEnvironment.IsRelease)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(cfg["Local:Username"], "Missing Local:Username configuration value.");
                ArgumentException.ThrowIfNullOrWhiteSpace(cfg["Local:Password"], "Missing Local:Password configuration value.");

                DseLocalEnvironment env = new()
                {
                    ApplicationName = hostEnv.ApplicationName,
                    ContentRootFileProvider = hostEnv.ContentRootFileProvider,
                    ContentRootPath = hostEnv.ContentRootPath,
                    EnvironmentName = hostEnv.EnvironmentName,
                };
                cfg.Bind("Local", env);
                return env;
            }

            if (hostEnv.IsTest())
            {
                return new DseEnvironment
                {
                    ApplicationName = hostEnv.ApplicationName,
                    ContentRootFileProvider = hostEnv.ContentRootFileProvider,
                    ContentRootPath = hostEnv.ContentRootPath,
                    EnvironmentName = hostEnv.EnvironmentName,
                };
            }

            string? deploymentCfg = cfg[DeploymentEnvVar]?.Trim();
            if (!Enum.TryParse(deploymentCfg, ignoreCase: true, out DeploymentEnvironment deploymentEnv))
            {
                throw new InvalidOperationException($"Unrecognized {DeploymentEnvVar} value '{deploymentCfg}'.");
            }

            return new DseDeploymentEnvironment
            {
                ApplicationName = hostEnv.ApplicationName,
                ContentRootFileProvider = hostEnv.ContentRootFileProvider,
                ContentRootPath = hostEnv.ContentRootPath,
                EnvironmentName = hostEnv.EnvironmentName,
                Deployment = deploymentEnv,
            };
        });

    [ExcludeFromCodeCoverage]
    private class DseEnvironment : IDseEnvironment
    {
        public required string ApplicationName { get; set; }
        public required IFileProvider ContentRootFileProvider { get; set; }
        public required string ContentRootPath { get; set; }
        public required string EnvironmentName { get; set; }
    }

    [ExcludeFromCodeCoverage]
    private sealed class DseDeploymentEnvironment : DseEnvironment, IDseDeploymentEnvironment
    {
        public required DeploymentEnvironment Deployment { get; init; }
    }

    [ExcludeFromCodeCoverage]
    private sealed class DseLocalEnvironment : DseEnvironment, IDseLocalEnvironment
    {
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public string[] Roles { get; init; } = [];
    }
}
