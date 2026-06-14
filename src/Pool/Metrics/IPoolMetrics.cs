namespace Pool.Metrics;

/// <summary>
/// Interface for recording pool metrics. Implementations can forward metrics to various monitoring systems
/// such as OpenTelemetry, Prometheus, or logging frameworks.
/// <para>
/// The default implementation publishes every pool under one stable meter (<see cref="PoolMeter.Name"/>)
/// and carries the pool's identity as a <c>pool.name</c> tag rather than embedding it in the instrument
/// name, so measurements from all pools aggregate and a single <c>AddMeter</c> call captures them.
/// </para>
/// Counter: pool.lease.exceptions (tags: pool.name, error.type)
/// Counter: pool.preparation.exceptions (tags: pool.name, error.type)
/// Histogram: pool.lease.wait.duration (s; tag: pool.name)
/// Histogram: pool.item.preparation.duration (s; tag: pool.name)
/// Observable Up/Down Counter: pool.items.allocated (tag: pool.name)
/// Observable Up/Down Counter: pool.items.available (tag: pool.name)
/// Observable Up/Down Counter: pool.leases.active (tag: pool.name)
/// Observable Up/Down Counter: pool.leases.queued (tag: pool.name)
/// Observable Gauge: pool.utilization (tag: pool.name)
/// </summary>
/// <remarks>
/// <para>
/// Each <c>Register*Observer</c> call returns an <see cref="IDisposable"/>. The pool holds these
/// handles for its lifetime and disposes them when the pool is disposed. .NET instruments cannot be
/// removed from a meter, so disposal instead severs the observation callback: a disposed pool stops
/// reporting and is no longer kept alive by the (often longer-lived) meter's observation callbacks.
/// </para>
/// For more information about dotnet metrics, see https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation
/// </remarks>
public interface IPoolMetrics
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
    /// <returns>A handle that unregisters the observer when disposed.</returns>
    IDisposable RegisterItemsAllocatedObserver(Func<int> observeValue);

    /// <summary>
    /// Registers an observer for the number of items available in the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    /// <returns>A handle that unregisters the observer when disposed.</returns>
    IDisposable RegisterItemsAvailableObserver(Func<int> observeValue);

    /// <summary>
    /// Registers an observer for the number of active leases in the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    /// <returns>A handle that unregisters the observer when disposed.</returns>
    IDisposable RegisterActiveLeasesObserver(Func<int> observeValue);

    /// <summary>
    /// Registers an observer for the number of queued leases in the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    /// <returns>A handle that unregisters the observer when disposed.</returns>
    IDisposable RegisterQueuedLeasesObserver(Func<int> observeValue);

    /// <summary>
    /// Registers an observer for the utilization rate of the pool.
    /// </summary>
    /// <param name="observeValue"></param>
    /// <returns>A handle that unregisters the observer when disposed.</returns>
    IDisposable RegisterUtilizationRateObserver(Func<double> observeValue);
}
