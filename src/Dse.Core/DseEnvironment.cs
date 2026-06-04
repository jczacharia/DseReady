// Copyright (c) PNC Financial Services. All rights reserved.


using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

internal static class DseEnvironmentExtensions
{
    private const string DeploymentEnvVar = "DEPLOYMENT_ENVIRONMENT";

    public static void AddDseEnvironment(this IServiceCollection services)
    {
        if (IDseEnvironment.IsRelease)
        {
            services.AddSingleton<IDseEnvironment, DseDeploymentEnvironment>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var hostEnv = sp.GetRequiredService<IHostEnvironment>();

                string? deploymentCfg = cfg[DeploymentEnvVar]?.Trim();
                if (!Enum.TryParse<DeploymentEnvironment>(deploymentCfg, ignoreCase: true, out var deploymentEnv))
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
        }
        else
        {
            services.AddSingleton<IDseEnvironment, DseLocalEnvironment>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var hostEnv = sp.GetRequiredService<IHostEnvironment>();

                ArgumentException.ThrowIfNullOrWhiteSpace(cfg["Local:Username"], "Missing Local:Username configuration value.");
                ArgumentException.ThrowIfNullOrWhiteSpace(cfg["Local:Password"], "Missing Local:Password configuration value.");

                var env = new DseLocalEnvironment
                {
                    ApplicationName = hostEnv.ApplicationName,
                    ContentRootFileProvider = hostEnv.ContentRootFileProvider,
                    ContentRootPath = hostEnv.ContentRootPath,
                    EnvironmentName = hostEnv.EnvironmentName,
                };
                cfg.Bind("Local", env);
                return env;
            });
        }
    }

    private class DseDeploymentEnvironment : IDseDeploymentEnvironment
    {
        public required string ApplicationName { get; set; }
        public required IFileProvider ContentRootFileProvider { get; set; }
        public required string ContentRootPath { get; set; }
        public required string EnvironmentName { get; set; }
        public required DeploymentEnvironment Deployment { get; init; }
    }

    private sealed class DseLocalEnvironment : IDseLocalEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public string[] Roles { get; init; } = [];
    }
}
