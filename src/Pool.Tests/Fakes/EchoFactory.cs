﻿
namespace Pool.Tests.Fakes;

internal sealed class EchoFactory
    : IPoolItemFactory<IEcho>
    , IPoolItemReadyCheck<IEcho>
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "items created by the factory are disposed by the pool or the factory")]
    public IEcho CreateItem() => new Echo();

    public ValueTask<bool> IsReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => ValueTask.FromResult(item.IsConnected);

    public Task MakeReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => item.ConnectAsync(cancellationToken);
}
