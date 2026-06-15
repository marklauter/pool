using Pool.Metrics;

namespace Pool.Tests.Fakes;

// a do-nothing IPoolMetrics for tests that exercise pool/lease behavior rather than the metrics wiring
internal sealed class NoopPoolMetrics : IPoolMetrics
{
    public IDisposable RegisterItemsAllocatedObserver(Func<int> observeValue) => NoopDisposable.Instance;
    public IDisposable RegisterItemsAvailableObserver(Func<int> observeValue) => NoopDisposable.Instance;
    public IDisposable RegisterActiveLeasesObserver(Func<int> observeValue) => NoopDisposable.Instance;
    public IDisposable RegisterQueuedLeasesObserver(Func<int> observeValue) => NoopDisposable.Instance;
    public IDisposable RegisterUtilizationRateObserver(Func<double> observeValue) => NoopDisposable.Instance;
    public void RecordLeaseException(Exception ex) { }
    public void RecordPreparationException(Exception ex) { }
    public void RecordLeaseWaitTime(TimeSpan duration) { }
    public void RecordPreparationTime(TimeSpan duration) { }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
