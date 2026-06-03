// Copyright (c) PNC Financial Services. All rights reserved.


using FluentValidation;

namespace Dse.ES;

public sealed class ElasticOptions
{
    public const string SectionName = "Elastic";

    public string BaseAddress { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class ElasticOptionsValidator : AbstractValidator<ElasticOptions>
{
    public ElasticOptionsValidator() =>
        RuleFor(o => o.BaseAddress).NotEmpty().Must(uri => Uri.IsWellFormedUriString(uri, UriKind.Absolute));
}
