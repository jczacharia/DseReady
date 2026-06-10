// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Dse.ES;

public sealed class ElasticChangeTokenSource<TOptions> : IOptionsChangeTokenSource<TOptions>
{
    private CancellationTokenSource _cts = new();
    public string Name => Options.DefaultName;
    public IChangeToken GetChangeToken() => new CancellationChangeToken(_cts.Token);

    public void TriggerReload()
    {
        CancellationTokenSource old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }
}
