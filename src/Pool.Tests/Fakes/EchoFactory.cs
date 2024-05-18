
namespace Pool.Tests.Fakes;

internal sealed class EchoFactory
    : IPoolItemFactory<IEcho>
    , IReadyCheck<IEcho>
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "items created by the factory are disposed by the pool or the factory")]
    public IEcho CreateItem() => new Echo();

    public async Task<bool> IsReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => await Task.FromResult(item.IsReady);

    public async Task MakeReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => await item.MakeReadyAsync(cancellationToken);
}
