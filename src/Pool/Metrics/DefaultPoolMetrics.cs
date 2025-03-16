using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

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
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP006:Implement IDisposable", Justification = "IMeterFactory is reposible for disposal of meters. See https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation#best-practices-1")]
    private readonly Meter meter;
    private readonly ILogger<DefaultPoolMetrics> logger;

    public DefaultPoolMetrics(
        string name,
        IMeterFactory meterFactory,
        ILogger<DefaultPoolMetrics> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(meterFactory);

        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        meter = meterFactory.Create(name);

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
    }

    /// <inheritdoc/>
    public void RecordLeaseWaitTime(TimeSpan duration) =>
        leaseWaitTimeHistogram.Record(duration.TotalMilliseconds);

    /// <inheritdoc/>
    public void RecordPreparationTime(TimeSpan duration) =>
        preparationTimeHistogram.Record(duration.TotalMilliseconds);

    /// <inheritdoc/>
    public void RegisterItemsAllocatedObserver(Func<int> observeValue) =>
        itemsAllocatedCounter = meter.CreateObservableUpDownCounter(
            name: $"{meter.Name}.items_allocated",
            observeValue: observeValue,
            unit: "items",
            description: "Number of items currently allocated");

    /// <inheritdoc/>
    public void RegisterItemsAvailableObserver(Func<int> observeValue) =>
        itemsAvailableCounter = meter.CreateObservableUpDownCounter(
            name: $"{meter.Name}.items_available",
            observeValue: observeValue,
            unit: "items",
            description: "Number of items currently available");

    /// <inheritdoc/>
    public void RegisterActiveLeasesObserver(Func<int> observeValue) =>
        activeLeasesCounter = meter.CreateObservableUpDownCounter(
            name: $"{meter.Name}.active_leases",
            observeValue: observeValue,
            unit: "leases",
            description: "Number of active leases");

    /// <inheritdoc/>
    public void RegisterQueuedLeasesObserver(Func<int> observeValue) =>
        queuedLeasesCounter = meter.CreateObservableUpDownCounter(
            name: $"{meter.Name}.queued_leases",
            observeValue: observeValue,
            unit: "leases",
            description: "Number of queued leases");

    /// <inheritdoc/>
    public void RegisterUtilizationRateObserver(Func<double> observeValue) =>
        utilizationRateGauge = meter.CreateObservableGauge(
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
