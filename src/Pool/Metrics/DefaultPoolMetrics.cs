using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Pool.Metrics;

/// <inheritdoc/>
internal sealed class DefaultPoolMetrics
    : IPoolMetrics
    , IDisposable
{
    private readonly UpDownCounter<int> itemsAllocated;
    private readonly UpDownCounter<int> itemsAvailable;
    private readonly UpDownCounter<int> activeLeases;
    private readonly UpDownCounter<int> queuedLeases;
    private readonly Counter<ulong> preparationFailures;
    private readonly Counter<ulong> leaseFailures;
    private readonly Counter<ulong> itemsCreated;
    private readonly Counter<ulong> itemsDisposed;
    private readonly ObservableGauge<double> utilizationRate;
    private readonly Histogram<double> leaseWaitTime;
    private readonly Histogram<double> preparationTime;
    private readonly Meter meter;
    private bool disposed;

    public DefaultPoolMetrics(string name)
    {
        meter = new Meter(name);

        itemsAllocated = meter.CreateUpDownCounter<int>(
            name: $"{name}.pool_items_allocated",
            unit: "items",
            description: "Number of items allocated");

        itemsAvailable = meter.CreateUpDownCounter<int>(
            name: $"{name}.pool_items_available",
            unit: "items",
            description: "Number of items available");

        activeLeases = meter.CreateUpDownCounter<int>(
            name: $"{name}.pool_active_leases",
            unit: "leases",
            description: "Number of active leases");

        queuedLeases = meter.CreateUpDownCounter<int>(
            name: $"{name}.pool_queued_leases",
            unit: "leases",
            description: "Number of queued leases");

        leaseWaitTime = meter.CreateHistogram<double>(
            name: $"{name}.pool_lease_wait_time",
            unit: "ms",
            description: "Time spent waiting for pool item lease");

        preparationTime = meter.CreateHistogram<double>(
            name: $"{name}.pool_preparation_time",
            unit: "ms",
            description: "Time spent preparing pool items");

        preparationFailures = meter.CreateCounter<ulong>(
            name: $"{name}.pool_preparation_failures",
            unit: "attempts",
            description: "Number of preparation failures");

        leaseFailures = meter.CreateCounter<ulong>(
            name: $"{name}.pool_lease_failures",
            unit: "attempts",
            description: "Number of lease failures");

        itemsCreated = meter.CreateCounter<ulong>(
            name: $"{name}.pool_items_created",
            unit: "items",
            description: "Number of items created");

        itemsDisposed = meter.CreateCounter<ulong>(
            name: $"{name}.pool_items_disposed",
            unit: "items",
            description: "Number of items disposed");

        utilizationRate = meter.CreateObservableGauge<double>(
            name: $"{name}.pool_utilization_rate",
            observeValue: CalculateUtilizationRate,
            description: "Pool utilization rate (active/total)");
    }

    private double CalculateUtilizationRate() =>
        // Implement the logic to calculate the utilization rate
        // This is just a placeholder implementation
        0.0;

    /// <inheritdoc/>
    public void RecordLeaseWaitTime(TimeSpan duration) =>
        ThrowIfDisposed().leaseWaitTime.Record(duration.TotalMilliseconds);

    /// <inheritdoc/>
    public void RecordPreparationTime(TimeSpan duration) =>
        ThrowIfDisposed().preparationTime.Record(duration.TotalMilliseconds);

    /// <inheritdoc/>
    public void RecordLeaseFailure() =>
        ThrowIfDisposed().leaseFailures.Add(1);

    /// <inheritdoc/>
    public void RecordPreparationFailure() =>
        ThrowIfDisposed().preparationFailures.Add(1);

    /// <inheritdoc/>
    public void RecordItemCreated() =>
        ThrowIfDisposed().itemsCreated.Add(1);

    /// <inheritdoc/>
    public void RecordItemDisposed() =>
        ThrowIfDisposed().itemsDisposed.Add(1);

    ///// <inheritdoc/>
    //public void RecordPoolUtilization(int activeLeases, int totalItems)
    //{
    //    if (totalItems > 0)
    //    {
    //        ThrowIfDisposed().utilizationRate.Record((double)activeLeases / totalItems);
    //    }
    //}

    /// <inheritdoc/>
    public void RecordRelease() =>
        ThrowIfDisposed().itemsDisposed.Add(1);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        meter.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DefaultPoolMetrics ThrowIfDisposed() => disposed
        ? throw new ObjectDisposedException(nameof(DefaultPoolMetrics))
        : this;
    public void RecordPoolUtilization(int activeLeases, int totalItems) => throw new NotImplementedException();
}
