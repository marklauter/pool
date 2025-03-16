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
    private ObservableUpDownCounter<int>? itemsAllocatedCounter;
    private ObservableUpDownCounter<int>? itemsAvailableCounter;
    private ObservableUpDownCounter<int>? activeLeasesCounter;
    private ObservableUpDownCounter<int>? queuedLeasesCounter;
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
            name: $"{name}.lease_exception",
            unit: "exceptions",
            description: "Number of exceptions thrown during pool item lease");

        preparationExceptionCounter = meter.CreateCounter<long>(
            name: $"{name}.preparation_exception",
            unit: "exceptions",
            description: "Number of exceptions thrown during pool item preparation");

        leaseWaitTimeHistogram = meter.CreateHistogram<double>(
            name: $"{name}.lease_wait_time",
            unit: "ms",
            description: "Time spent waiting for item lease");

        preparationTimeHistogram = meter.CreateHistogram<double>(
            name: $"{name}.item_preparation_time",
            unit: "ms",
            description: "Time spent preparing pool items");
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public void RecordLeaseWaitTime(TimeSpan duration) =>
        IsNotDisposed().leaseWaitTimeHistogram.Record(duration.TotalMilliseconds);

    /// <inheritdoc/>
    public void RecordPreparationTime(TimeSpan duration) =>
        IsNotDisposed().preparationTimeHistogram.Record(duration.TotalMilliseconds);

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
    private DefaultPoolMetrics IsNotDisposed() => disposed
        ? throw new ObjectDisposedException(nameof(DefaultPoolMetrics))
        : this;

    /// <inheritdoc/>
    public void RegisterItemsAllocatedObserver(Func<int> observeValue) =>
        itemsAllocatedCounter = IsNotDisposed().meter.CreateObservableUpDownCounter(
            name: $"{meter.Name}.items_allocated",
            observeValue: observeValue,
            unit: "items",
            description: "Number of items currently allocated");

    /// <inheritdoc/>
    public void RegisterItemsAvailableObserver(Func<int> observeValue) =>
        itemsAvailableCounter = IsNotDisposed().meter.CreateObservableUpDownCounter(
            name: $"{meter.Name}.items_available",
            observeValue: observeValue,
            unit: "items",
            description: "Number of items currently available");

    /// <inheritdoc/>
    public void RegisterActiveLeasesObserver(Func<int> observeValue) =>
        activeLeasesCounter = IsNotDisposed().meter.CreateObservableUpDownCounter(
            name: $"{meter.Name}.active_leases",
            observeValue: observeValue,
            unit: "leases",
            description: "Number of active leases");

    /// <inheritdoc/>
    public void RegisterQueuedLeasesObserver(Func<int> observeValue) =>
        queuedLeasesCounter = IsNotDisposed().meter.CreateObservableUpDownCounter(
            name: $"{meter.Name}.queued_leases",
            observeValue: observeValue,
            unit: "leases",
            description: "Number of queued leases");

    /// <inheritdoc/>
    public void RegisterUtilizationRateObserver(Func<double> observeValue) =>
        utilizationRateGauge = IsNotDisposed().meter.CreateObservableGauge(
            name: $"{meter.Name}.utilization_rate",
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
