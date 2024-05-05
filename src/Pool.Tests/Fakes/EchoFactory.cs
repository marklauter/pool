
namespace Pool.Tests.Fakes;

internal sealed class EchoFactory
    : IPoolItemFactory<IEcho>
    , IReadyCheck<IEcho>

{
    public IEcho CreateItem()
    {
        return new Echo();
    }

    public async Task<bool> IsReadyAsync(
        IEcho item,
        CancellationToken cancellationToken)
    {
        return await Task.FromResult(item.IsReady);
    }

    public async Task MakeReadyAsync(
        IEcho item,
        CancellationToken cancellationToken)
    {
        await item.MakeReadyAsync(cancellationToken);
    }
}
