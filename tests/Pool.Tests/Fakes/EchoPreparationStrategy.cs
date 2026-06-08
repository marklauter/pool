
namespace Pool.Tests.Fakes;

internal sealed class EchoPreparationStrategy
    : IPreparationStrategy<IEcho>
{
    public ValueTask<bool> IsReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => ValueTask.FromResult(item.IsConnected);

    public Task PrepareAsync(
        IEcho item,
        CancellationToken cancellationToken) => item.ConnectAsync(cancellationToken);
}
