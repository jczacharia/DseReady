// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Auth;
using FluentValidation;
using Humanizer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dse.Shared;

public enum DseEnvironmentName
{
    Dev,
    Test,
    Rnd,
    Uat,
    Qa,
    Prod,
}

public sealed class DseOptions
{
    public static bool IsCi() => System.Environment.GetEnvironmentVariable("CI")?.Trim() switch
    {
        "1" => true,
        var val => bool.TryParse(val, out bool isCi) && isCi,
    };

    public DseEnvironmentName Environment { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string[] Roles { get; set; } = [];

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

        RuleFor(o => o.Environment).IsInEnum();

        if (env.IsProduction())
        {
            RuleFor(o => o.Environment)
                .Must(v => v is DseEnvironmentName.Rnd or
                                DseEnvironmentName.Uat or
                                DseEnvironmentName.Qa or
                                DseEnvironmentName.Prod)
                .WithMessage("In Production, Value must be one of the following: Dev, Rnd, Uat, Qa, Prod.");
        }
        else if (env.IsDevelopment())
        {
            RuleFor(o => o.Environment).Must(v => v is DseEnvironmentName.Dev);
        }
        else if (env.IsTest())
        {
            RuleFor(o => o.Environment).Must(v => v is DseEnvironmentName.Test);
        }
    }
}

public static class DseEnvironmentExtensions
{
    public static bool IsTest(this IHostEnvironment env) => env.IsEnvironment("Test");
    public static bool IsLocal(this IHostEnvironment env) => env.IsDevelopment() || env.IsTest() && !DseOptions.IsCi();

    public static void AddDseEnvironment(this IHostApplicationBuilder builder)
    {
        if (builder.Environment.IsLocal())
        {
            builder.Configuration.AddUserSecrets("dse", reloadOnChange: true);
        }


        if (builder.Environment.IsProduction() && builder.Configuration["DEPLOYMENT_ENVIRONMENT"] is { } deploymentEnv)
        {
            builder.Configuration.AddJsonFile($"deployment.{deploymentEnv.Trim().Pascalize()}.json",
                optional: true,
                reloadOnChange: false);
        }

        builder.Services
            .AddOptions<DseOptions>()
            .BindConfiguration("Dse")
            .Configure(options =>
            {
                if (!builder.Environment.IsLocal())
                {
                    options.Username = null;
                    options.Password = null;
                }

                if (builder.Environment.IsDevelopment())
                {
                    options.Environment = DseEnvironmentName.Dev;
                }

                if (builder.Environment.IsTest())
                {
                    options.Environment = DseEnvironmentName.Test;
                }
            })
            .WithFluentValidator<DseOptions, DseEnvironmentValidator>();
    }

    public static OptionsBuilder<TOptions> PostDseConfigure<TOptions>(
        this OptionsBuilder<TOptions> builder,
        Action<TOptions, DseOptions> configure) where TOptions : class =>
        builder.PostConfigure<IOptions<DseOptions>>((options, dseEnv) => configure(options, dseEnv.Value));
}
