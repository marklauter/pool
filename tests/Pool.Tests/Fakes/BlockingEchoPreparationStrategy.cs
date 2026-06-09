namespace Pool.Tests.Fakes;

// never reports ready and blocks in PrepareAsync until the token cancels, so a test can drive a
// preparation timeout deterministically with a FakeTimeProvider instead of a real wait (findings M4)
internal sealed class BlockingEchoPreparationStrategy
    : IPreparationStrategy<IEcho>
{
    public ValueTask<bool> IsReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public Task PrepareAsync(
        IEcho item,
        CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
}
