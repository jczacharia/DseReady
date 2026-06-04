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
    public string Username { get; }
    public string Password { get; }
}

internal static class DseEnvironmentExtensions
{
    private const string DeploymentEnvVar = "DEPLOYMENT_ENVIRONMENT";

    public static void AddDseEnvironment(this IServiceCollection services)
    {
        if (IDseEnvironment.IsRelease)
        {
            services.TryAddSingleton<IDseEnvironment>(sp =>
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
            services.TryAddSingleton<IDseEnvironment>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var hostEnv = sp.GetRequiredService<IHostEnvironment>();

                ArgumentException.ThrowIfNullOrWhiteSpace(cfg["Local:Username"], "Missing Local:Username configuration value.");
                ArgumentException.ThrowIfNullOrWhiteSpace(cfg["Local:Password"], "Missing Local:Password configuration value.");

                return new DseLocalEnvironment
                {
                    ApplicationName = hostEnv.ApplicationName,
                    ContentRootFileProvider = hostEnv.ContentRootFileProvider,
                    ContentRootPath = hostEnv.ContentRootPath,
                    EnvironmentName = hostEnv.EnvironmentName,
                    Username = cfg["Local:Username"]!,
                    Password = cfg["Local:Password"]!,
                };
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
        public required string ApplicationName { get; set; }
        public required IFileProvider ContentRootFileProvider { get; set; }
        public required string ContentRootPath { get; set; }
        public required string EnvironmentName { get; set; }
        public required string Username { get; init; }
        public required string Password { get; init; }
    }
}
