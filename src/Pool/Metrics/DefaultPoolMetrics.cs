using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Pool.Metrics;

/// <inheritdoc/>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "don't need performance in the exception handlers")]
internal sealed class DefaultPoolMetrics
    : IPoolMetrics
{
    private ObservableCounter<int>? itemsAllocatedCounter;
    private ObservableCounter<int>? itemsAvailableCounter;
    private ObservableCounter<int>? activeLeasesCounter;
    private ObservableCounter<int>? queuedLeasesCounter;
    private ObservableGauge<double>? utilizationRateGauge;
    private readonly Counter<long> leaseExceptionCounter;
    private readonly Counter<long> preparationExceptionCounter;
    private readonly Histogram<double> leaseWaitTimeHistogram;
    private readonly Histogram<double> preparationTimeHistogram;
    private readonly Meter meter;
    private readonly ILogger<DefaultPoolMetrics> logger;
    private bool disposed;

    public DefaultPoolMetrics(
        string name,
        ILogger<DefaultPoolMetrics> logger)
    {
        meter = new Meter(name);

        leaseExceptionCounter = meter.CreateCounter<long>(
            name: $"{name}.pool_lease_exception",
            unit: "exceptions",
            description: "Number of exceptions thrown during pool item lease");

        preparationExceptionCounter = meter.CreateCounter<long>(
            name: $"{name}.pool_preparation_exception",
            unit: "exceptions",
            description: "Number of exceptions thrown during pool item preparation");

        leaseWaitTimeHistogram = meter.CreateHistogram<double>(
            name: $"{name}.pool_lease_wait_time",
            unit: "ms",
            description: "Time spent waiting for pool item lease");

        preparationTimeHistogram = meter.CreateHistogram<double>(
            name: $"{name}.pool_preparation_time",
            unit: "ms",
            description: "Time spent preparing pool items");
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public void RecordLeaseWaitTime(TimeSpan duration) =>
        ThrowIfDisposed().leaseWaitTimeHistogram.Record(duration.TotalMilliseconds);

    /// <inheritdoc/>
    public void RecordPreparationTime(TimeSpan duration) =>
        ThrowIfDisposed().preparationTimeHistogram.Record(duration.TotalMilliseconds);

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

    /// <inheritdoc/>
    public void RegisterItemsAllocatedObserver(Func<int> observeValue) =>
        itemsAllocatedCounter = ThrowIfDisposed().meter.CreateObservableCounter(
            name: $"{meter.Name}.pool_items_allocated",
            observeValue: observeValue,
            unit: "items",
            description: "Number of items allocated");

    /// <inheritdoc/>
    public void RegisterItemsAvailableObserver(Func<int> observeValue) =>
        itemsAvailableCounter = ThrowIfDisposed().meter.CreateObservableCounter(
            name: $"{meter.Name}.pool_items_available",
            observeValue: observeValue,
            unit: "items",
            description: "Number of items available");

    /// <inheritdoc/>
    public void RegisterActiveLeasesObserver(Func<int> observeValue) =>
        activeLeasesCounter = ThrowIfDisposed().meter.CreateObservableCounter(
            name: $"{meter.Name}.pool_active_leases",
            observeValue: observeValue,
            unit: "leases",
            description: "Number of active leases");

    /// <inheritdoc/>
    public void RegisterQueuedLeasesObserver(Func<int> observeValue) =>
        queuedLeasesCounter = ThrowIfDisposed().meter.CreateObservableCounter(
            name: $"{meter.Name}.pool_queued_leases",
            observeValue: observeValue,
            unit: "leases",
            description: "Number of queued leases");

    /// <inheritdoc/>
    public void RegisterUtilizationRateObserver(Func<double> observeValue) =>
        utilizationRateGauge = ThrowIfDisposed().meter.CreateObservableGauge(
            name: $"{meter.Name}.pool_utilization_rate",
            observeValue: observeValue,
            description: "Pool utilization rate (active/total)");

    /// <inheritdoc/>
    public void RecordLeaseException(Exception ex)
    {
        leaseExceptionCounter.Add(1);
        logger.LogError(ex, "An exception occurred while leasing a pool item.");
    }

    /// <inheritdoc/>
    public void RecordPreparationException(Exception ex)
    {
        preparationExceptionCounter.Add(1);
        logger.LogError(ex, "An exception occurred while preparing a pool item.");
    }
}
