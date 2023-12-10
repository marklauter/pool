
namespace Pool.Tests.Fakes;

internal sealed class EchoFactory
    : IPoolItemFactory<IEcho>
{
    public IEcho Create()
    {
        return new Echo();
    }

    public async Task<bool> IsReadyAsync(IEcho item, CancellationToken cancellationToken)
    {
        return await Task.FromResult(item.IsReady);
    }

    public async Task MakeReadyAsync(IEcho item, CancellationToken cancellationToken)
    {
        item.MakeReady();
        await Task.CompletedTask;
    }
}
