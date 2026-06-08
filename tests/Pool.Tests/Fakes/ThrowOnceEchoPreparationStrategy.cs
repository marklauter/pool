namespace Pool.Tests.Fakes;

/// <summary>
/// Models a connection whose first preparation fails - e.g. a pooled SMTP client whose
/// socket the server dropped and which cannot reconnect - and which prepares successfully
/// on later attempts. Captures the item from the failing attempt so a test can assert the
/// pool discarded and disposed it rather than recirculating it.
/// </summary>
internal sealed class ThrowOnceEchoPreparationStrategy
    : IPreparationStrategy<IEcho>
{
    private int prepareCount;

    public IEcho? FailedItem { get; private set; }

    public ValueTask<bool> IsReadyAsync(
        IEcho item,
        CancellationToken cancellationToken) => ValueTask.FromResult(item.IsConnected);

    public Task PrepareAsync(
        IEcho item,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref prepareCount) == 1)
        {
            FailedItem = item;
            throw new InvalidOperationException("simulated dropped socket");
        }

        return item.ConnectAsync(cancellationToken);
    }
}
