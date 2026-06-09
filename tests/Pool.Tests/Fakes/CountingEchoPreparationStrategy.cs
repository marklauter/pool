namespace Pool.Tests.Fakes;

// counts PrepareAsync invocations so a test can prove the pool actually invoked preparation for an
// unprepared item and skipped it for an already-ready one, rather than re-deriving readiness (findings M3)
internal sealed class CountingEchoPreparationStrategy
    : IPreparationStrategy<IEcho>
{
    public int PrepareCount { get; private set; }

    public ValueTask<bool> IsReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => ValueTask.FromResult(item.IsConnected);

    public Task PrepareAsync(
        IEcho item,
        CancellationToken cancellationToken)
    {
        PrepareCount++;
        return item.ConnectAsync(cancellationToken);
    }
}
