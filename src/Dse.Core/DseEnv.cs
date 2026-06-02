// Copyright (c) PNC Financial Services. All rights reserved.


// using System.Globalization;
// using Microsoft.Extensions.Configuration;
// using Microsoft.Extensions.DependencyInjection;
// using Microsoft.Extensions.Hosting;
//
// namespace Dse;
//
// public enum DseEnv
// {
//     Dev,
//     Test,
//     Rnd,
//     Uat,
//     Qa,
//     Prod,
// }
//
// public sealed record DseEnvironment(DseEnv Value)
// {
//     public bool IsDev => Value == DseEnv.Dev;
//     public bool IsTest => Value == DseEnv.Test;
//     public bool IsRnd => Value == DseEnv.Rnd;
//     public bool IsUat => Value == DseEnv.Uat;
//     public bool IsQa => Value == DseEnv.Qa;
//     public bool IsProd => Value == DseEnv.Prod;
//     public bool IsDeployment => Value is DseEnv.Rnd or DseEnv.Uat or DseEnv.Qa or DseEnv.Prod;
//
//     public const string DeploymentTierEnvVarName = "DEPLOYMENT_ENVIRONMENT";
//
//     /// <summary>
//     ///     Resolves the tier. Local dev and integration tests follow ASPNETCORE_ENVIRONMENT; deployed tiers run as
//     ///     "Production" and name themselves via <see cref="DeploymentTierEnvVarName" />. Throws — never guesses a default —
//     ///     because a wrong environment identity (writing to the wrong indices, running the wrong schedule) is far
//     ///     worse than a pod that refuses to start.
//     /// </summary>
//     public static DseEnvironment From(IConfiguration cfg, IHostEnvironment host)
//     {
//         if (host.IsDevelopment())
//         {
//             return new DseEnvironment(DseEnv.Dev);
//         }
//
//         if (host.EnvironmentName == "Test")
//         {
//             return new DseEnvironment(DseEnv.Test);
//         }
//
//         if (Enum.TryParse(cfg[DeploymentTierEnvVarName], ignoreCase: true, out DseEnv env))
//         {
//             return new DseEnvironment(env);
//         }
//
//         throw new InvalidOperationException($"Unrecognized {DeploymentTierEnvVarName} value '{cfg[DeploymentTierEnvVarName]}'.");
//     }
// }
//
// public static class DseEnvironmentExtensions
// {
//     /// <summary>
//     ///     Resolves and registers <see cref="DseEnvironment" /> at startup (fail-fast). A misconfigured tier
//     ///     throws here in the composition root, with the cause written synchronously to stderr — so a deployed pod
//     ///     surfaces it in its logs instead of crash-looping silently the way the old lazy DI factory did.
//     /// </summary>
//     public static IServiceCollection AddDseEnvironment(
//         this IServiceCollection services,
//         IConfiguration cfg,
//         IHostEnvironment host)
//     {
//         DseEnvironment environment;
//         try
//         {
//             environment = DseEnvironment.From(cfg, host);
//         }
//         catch (Exception ex)
//         {
//             // Console.Error flushes immediately, so the message survives a crash even when a buffered logger
//             // (which only comes up later, after Build) never gets the chance to flush.
//             Console.Error.WriteLine($"[FATAL] DseEnvironment resolution failed: {ex.Message}");
//             throw;
//         }
//
//         return services.AddSingleton(environment);
//     }
// }



