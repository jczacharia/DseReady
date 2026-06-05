// Copyright (c) PNC Financial Services. All rights reserved.


using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dse.Shared;

public static class FluentOptions
{
    public static OptionsBuilder<TOptions> AddFluentOptions<TOptions>(this IServiceCollection services, string sectionName)
        where TOptions : class
    {
        OptionsBuilder<TOptions> builder = services.AddOptions<TOptions>()
            .BindConfiguration(sectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddTransient<TOptions>(sp => sp.GetRequiredService<IOptions<TOptions>>().Value);
        return builder;
    }

    // A NAMED options instance whose name doubles as its configuration section path. One key (e.g. "Ldap:Ad") then
    // identifies the options everywhere — at binding, at IOptionsMonitor.Get(name), and as the keyed-service key of
    // whatever consumes it. Use when several instances of one options type must coexist (AD vs OUD). No default
    // IOptions<T>/transient is registered: with multiple instances an un-named resolve would be ambiguous.
    public static OptionsBuilder<TOptions> AddNamedFluentOptions<TOptions>(this IServiceCollection services, string name)
        where TOptions : class =>
        services.AddOptions<TOptions>(name)
            .BindConfiguration(name)
            .ValidateDataAnnotations()
            .ValidateOnStart();

    public static OptionsBuilder<TOptions> WithFluentValidator<TOptions, TValidator>(this OptionsBuilder<TOptions> builder)
        where TOptions : class
        where TValidator : class, IValidator<TOptions>
    {
        builder.Services.AddScoped<TValidator>();
        builder.Services.AddScoped<IValidator<TOptions>>(sp => sp.GetRequiredService<TValidator>());
        builder.Services.AddSingleton<IValidateOptions<TOptions>>(sp => new FluentValidateOptions<TOptions>(sp, builder.Name));
        return builder;
    }

    private sealed class FluentValidateOptions<TOptions>(IServiceProvider sp, string? optionsName)
        : IValidateOptions<TOptions> where TOptions : class
    {
        public ValidateOptionsResult Validate(string? name, TOptions options)
        {
            if (optionsName is not null && optionsName != name)
            {
                return ValidateOptionsResult.Skip;
            }

            ArgumentNullException.ThrowIfNull(options);

            using IServiceScope scope = sp.CreateScope();

            List<string> errors = [];
            string type = options.GetType().Name;

            foreach (IValidator<TOptions> validator in scope.ServiceProvider.GetServices<IValidator<TOptions>>())
            {
                if (validator.Validate(options) is { IsValid: false } result)
                {
                    errors.AddRange(result.Errors.Select(e =>
                        $"Validation for {type}.{e.PropertyName} failed: {e.ErrorMessage}"));
                }
            }

            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}
