// // Copyright (c) PNC Financial Services. All rights reserved.
//
//
// using Microsoft.Extensions.Configuration;
// using Microsoft.Extensions.Hosting;
//
// namespace Dse.Shared;
//
// public sealed record DeploymentEnvironment(string Value)
// {
//     public const string Rnd = "RND";
//     public const string Uat = "UAT";
//     public const string Qa = "QA";
//     public const string Prod = "PROD";
// }
//
// public static class CoreEnvironment
// {
//     private const string DeploymentEnvVar = "DEPLOYMENT_ENVIRONMENT";
//
//     public static bool IsTest(this IHostEnvironment env) => env.IsEnvironment("Test");
//     public static bool IsLocal(this IHostEnvironment env) => env.IsDevelopment() || env.IsTest() && !IsCi();
//
//     public static bool IsCi() => Environment.GetEnvironmentVariable("CI")?.Trim() switch
//     {
//         "1" => true,
//         var val => bool.TryParse(val, out bool isCi) && isCi,
//     };
//
//     public static string? GetDeploymentEnvironment(this IConfiguration cfg) => cfg[DeploymentEnvVar]?.Trim().ToUpperInvariant();
//
//     public static void AddCoreEnvironment(this IHostApplicationBuilder builder)
//     {
//         if (builder.Environment.IsProduction())
//         {
//             string? deploymentEnv = builder.Configuration.GetDeploymentEnvironment();
//
//             if (string.IsNullOrWhiteSpace(deploymentEnv))
//             {
//                 throw new InvalidOperationException($"{DeploymentEnvVar} is required in Production but was empty.");
//             }
//
//             if (deploymentEnv is not DeploymentEnvironment.Rnd and
//                                  not DeploymentEnvironment.Uat and
//                                  not DeploymentEnvironment.Qa and
//                                  not DeploymentEnvironment.Prod)
//             {
//                 throw new InvalidOperationException(
//                     $"{DeploymentEnvVar} must be one of the following values: {DeploymentEnvironment.Rnd}, "
//                     + $"{DeploymentEnvironment.Uat}, {DeploymentEnvironment.Qa}, {DeploymentEnvironment.Prod}.");
//             }
//
//             builder.Configuration.AddJsonFile($"deployment.{deploymentEnv}.json", optional: false, reloadOnChange: false);
//         }
//
//         if (builder.Environment.IsLocal())
//         {
//             builder.Configuration.AddUserSecrets("dse", reloadOnChange: true);
//         }
//     }
// }
