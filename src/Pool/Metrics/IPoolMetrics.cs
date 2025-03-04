namespace Pool.Metrics;

/// <summary>
/// Interface for recording pool metrics. Implementations can forward metrics to various monitoring systems
/// such as OpenTelemetry, Prometheus, or logging frameworks.
/// </summary>
public interface IPoolMetrics
{
    /// <summary>
    /// Records the duration a caller waited to acquire a pool item.
    /// This metric helps track pool responsiveness and potential bottlenecks.
    /// </summary>
    /// <param name="duration">The time span between requesting and receiving a pool item.</param>
    void RecordLeaseWaitTime(TimeSpan duration);

    /// <summary>
    /// Records when a lease request fails.
    /// This metric helps track reliability issues with pool items.
    /// </summary>
    void RecordLeaseFailure();

    /// <summary>
    /// Records the duration spent preparing a pool item before it can be used.
    /// This metric helps track the overhead of item preparation strategies.
    /// </summary>
    /// <param name="duration">The time span required to prepare the item.</param>
    void RecordPreparationTime(TimeSpan duration);

    /// <summary>
    /// Records when an item preparation attempt fails.
    /// This metric helps track reliability issues with pool items.
    /// </summary>
    void RecordPreparationFailure();

    /// <summary>
    /// Records when a new item is created and added to the pool.
    /// This metric helps track pool growth and resource allocation.
    /// </summary>
    void RecordItemCreated();

    /// <summary>
    /// Records when an item is disposed and removed from the pool.
    /// This metric helps track pool cleanup and resource deallocation.
    /// </summary>
    void RecordItemDisposed();

    /// <summary>
    /// Records the current utilization state of the pool.
    /// This metric helps track pool efficiency and capacity planning.
    /// </summary>
    /// <param name="activeLeases">The number of items currently leased out.</param>
    /// <param name="totalItems">The total number of items managed by the pool.</param>
    void RecordPoolUtilization(int activeLeases, int totalItems);
}
