// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dse;

public enum DeploymentEnvironment
{
    Rnd,
    Uat,
    Qa,
    Prod,
}

public sealed class DseOptions
{
    public const string SectionName = "Dse";
    public const string DeploymentEnvVar = "DEPLOYMENT_ENVIRONMENT";

    public DeploymentEnvironment? Deployment { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string[] Roles { get; set; } = [];

    public static bool IsCi() => Environment.GetEnvironmentVariable("CI")?.Trim() switch
    {
        "1" => true,
        var val => bool.TryParse(val, out bool isCi) && isCi,
    };

    public (string Username, string Password, string[] Roles)? LocalCredentials() =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password)
            ? (Username, Password, Roles)
            : null;
}

public sealed class DseEnvironmentValidator : AbstractValidator<DseOptions>
{
    public DseEnvironmentValidator(IHostEnvironment env)
    {
        if (env.IsLocal())
        {
            RuleFor(o => o.Username).NotEmpty();
            RuleFor(o => o.Password).NotEmpty();
        }
    }
}

public static class DseOptionsExtensions
{
    public static bool IsTest(this IHostEnvironment env) => env.IsEnvironment("Test");
    public static bool IsLocal(this IHostEnvironment env) => env.IsDevelopment() || env.IsTest() && !DseOptions.IsCi();

    public static void AddDseOptions(this IHostApplicationBuilder builder)
    {
        if (builder.Environment.IsLocal())
        {
            builder.Configuration.AddUserSecrets("dse", reloadOnChange: true);
        }

        DeploymentEnvironment? deploymentEnv = null;

        if (builder.Environment.IsProduction())
        {
            if (!Enum.TryParse(builder.Configuration[DseOptions.DeploymentEnvVar], ignoreCase: true,
                    out DeploymentEnvironment parsed))
            {
                throw new InvalidOperationException(
                    $"Invalid or missing {DseOptions.DeploymentEnvVar} environment variable."
                    + $" Expected one of: {string.Join(", ", Enum.GetNames<DeploymentEnvironment>())}");
            }

            deploymentEnv = parsed;
            builder.Configuration.AddJsonFile($"deployment.{parsed:G}.json", optional: false, reloadOnChange: false);
        }

        builder.Services
            .AddOptions<DseOptions>()
            .BindConfiguration(DseOptions.SectionName)
            .Configure(options => options.Deployment = deploymentEnv)
            .WithFluentValidator<DseOptions, DseEnvironmentValidator>();
    }

    public static OptionsBuilder<TOptions> PostDseConfigure<TOptions>(
        this OptionsBuilder<TOptions> builder,
        Action<TOptions, DseOptions> configure) where TOptions : class =>
        builder.PostConfigure<IOptions<DseOptions>>((options, dseEnv) => configure(options, dseEnv.Value));
}
