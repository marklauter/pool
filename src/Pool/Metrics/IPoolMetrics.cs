namespace Pool.Metrics;

/// <summary>
/// Interface for recording pool metrics. Implementations can forward metrics to various monitoring systems
/// such as OpenTelemetry, Prometheus, or logging frameworks.
/// </summary>
public interface IPoolMetrics
    : IDisposable
{
    /// <summary>
    /// Records an exception that occurred while leasing a pool item.
    /// </summary>
    /// <param name="ex"></param>
    void RecordLeaseException(Exception ex);

    /// <summary>
    /// Records an exception that occurred while preparing a pool item.
    /// </summary>
    /// <param name="ex"></param>
    void RecordPreparationException(Exception ex);

    /// <summary>
    /// Records the duration a caller waited to acquire a pool item.
    /// This metric helps track pool responsiveness and potential bottlenecks.
    /// </summary>
    /// <param name="duration">The time span between requesting and receiving a pool item.</param>
    void RecordLeaseWaitTime(TimeSpan duration);

    /// <summary>
    /// Records the duration spent preparing a pool item before it can be used.
    /// This metric helps track the overhead of item preparation strategies.
    /// </summary>
    /// <param name="duration">The time span required to prepare the item.</param>
    void RecordPreparationTime(TimeSpan duration);

    /// <summary>
    /// Registers an observer for the number of items allocated in the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    void RegisterItemsAllocatedObserver(Func<int> observeValue);

    /// <summary>
    /// Registers an observer for the number of items available in the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    void RegisterItemsAvailableObserver(Func<int> observeValue);

    /// <summary>
    /// Registers an observer for the number of active leases in the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    void RegisterActiveLeasesObserver(Func<int> observeValue);

    /// <summary>
    /// Registers an observer for the number of queued leases in the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    void RegisterQueuedLeasesObserver(Func<int> observeValue);

    /// <summary>
    /// Registers an observer for the utilization rate of the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    void RegisterUtilizationRateObserver(Func<double> observeValue);
}
