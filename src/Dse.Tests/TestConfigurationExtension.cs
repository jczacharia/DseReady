// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Dse.Tests;

public sealed class TestConfigurationExtension(Action<IConfigurationBuilder>? configure = null) : IAlbaExtension
{
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task Start(IAlbaHost host) => Task.CompletedTask;

    public IHostBuilder Configure(IHostBuilder builder) =>
        builder.ConfigureHostConfiguration(config => configure?.Invoke(config));
}
