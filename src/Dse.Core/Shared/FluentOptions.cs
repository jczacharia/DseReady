// Copyright (c) PNC Financial Services. All rights reserved.


using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dse.Shared;

public static class FluentOptions
{
    public static OptionsBuilder<TOptions> WithFluentValidator<TOptions, TValidator>(this OptionsBuilder<TOptions> builder)
        where TOptions : class
        where TValidator : class, IValidator<TOptions>
    {
        builder.Services.AddScoped<IValidator<TOptions>, TValidator>();
        builder.Services.AddSingleton<IValidateOptions<TOptions>>(sp => new FluentValidateOptions<TOptions>(sp, builder.Name));
        return builder;
    }

    private sealed class FluentValidateOptions<TOptions>(IServiceProvider sp, string? optionsName) : IValidateOptions<TOptions>
        where TOptions : class
    {
        public ValidateOptionsResult Validate(string? name, TOptions options)
        {
            if (optionsName is not null && optionsName != name)
            {
                return ValidateOptionsResult.Skip;
            }

            ArgumentNullException.ThrowIfNull(options);

            using IServiceScope scope = sp.CreateScope();

            var validator = scope.ServiceProvider.GetRequiredService<IValidator<TOptions>>();

            if (validator.Validate(options) is not { IsValid: false } result)
            {
                return ValidateOptionsResult.Success;
            }

            string type = options.GetType().Name;
            return ValidateOptionsResult.Fail(result.Errors.Select(failure =>
                $"Validation failed for {type}.{failure.PropertyName} with the error: {failure.ErrorMessage}"));
        }
    }
}
