using Microsoft.Extensions.Time.Testing;

namespace Pool.Tests.Fakes;

// advances a FakeTimeProvider during PrepareAsync so a test can prove the lease-wait-time metric
// excludes preparation time (the two are recorded against separate instruments).
internal sealed class ClockAdvancingEchoPreparationStrategy(FakeTimeProvider timeProvider, TimeSpan prepDuration)
    : IPreparationStrategy<IEcho>
{
    public ValueTask<bool> IsReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => ValueTask.FromResult(item.IsConnected);

    public Task PrepareAsync(
        IEcho item,
        CancellationToken cancellationToken)
    {
        timeProvider.Advance(prepDuration);
        return item.ConnectAsync(cancellationToken);
    }
}
