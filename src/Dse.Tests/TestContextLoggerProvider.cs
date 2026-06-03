// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.Extensions.Logging;

namespace Dse.Tests;

internal sealed class TestContextLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestContextLogger(categoryName);
    public void Dispose() { }

    private sealed class TestContextLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => TestContext.Current.TestOutputHelper is not null;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (TestContext.Current.TestOutputHelper is { } sink)
            {
                sink.WriteLine($"[{level}] {category}: {fmt(state, ex)}{(ex is null ? "" : Environment.NewLine + ex)}");
            }
        }
    }
}
