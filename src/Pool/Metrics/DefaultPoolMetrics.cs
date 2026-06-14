using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace Pool.Metrics;

/// <inheritdoc/>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "don't need performance in the exception handlers")]
internal sealed class DefaultPoolMetrics
    : IPoolMetrics
{
    // stamp the meter with the library version so OTEL consumers can attribute instruments to a release
    private static readonly string? MeterVersion = typeof(DefaultPoolMetrics).Assembly.GetName().Version?.ToString();

    // OTEL convention reports durations in seconds. The SDK's default histogram bucket boundaries are
    // tuned for milliseconds, so in seconds nearly every pool latency would collapse into the first
    // bucket. These boundaries (seconds) span sub-millisecond to ten seconds, the range pool lease and
    // preparation latencies actually occupy, so the histogram keeps useful resolution.
    private static readonly InstrumentAdvice<double> DurationBucketsAdvice = new()
    {
        HistogramBucketBoundaries = [0.0005, 0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 7.5, 10],
    };

    // observable instruments are not held in fields here. .NET instruments are not individually
    // removable from a meter, so each Register* call returns a severable handle (ObserverRegistration)
    // that roots the instrument while the pool lives and, on disposal, drops the value callback —
    // stopping the instrument from reporting and releasing the pool graph the callback captured.
    private readonly Counter<long> leaseExceptionCounter;
    private readonly Counter<long> preparationExceptionCounter;
    private readonly Histogram<double> leaseWaitTimeHistogram;
    private readonly Histogram<double> preparationTimeHistogram;
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP006:Implement IDisposable", Justification = "IMeterFactory is reposible for disposal of meters. See https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation#best-practices-1")]
    private readonly Meter meter;
    private readonly ILogger<DefaultPoolMetrics> logger;

    // pool identity rides as a tag rather than living in the instrument name, so every pool's
    // measurements aggregate under the one stable meter and OTEL consumers subscribe once via
    // PoolMeter.Name instead of enumerating a meter per pool.
    private readonly KeyValuePair<string, object?> poolNameTag;

    public DefaultPoolMetrics(
        string name,
        IMeterFactory meterFactory,
        ILogger<DefaultPoolMetrics> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(meterFactory);

        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        poolNameTag = new KeyValuePair<string, object?>("pool.name", name);

        // every pool shares this one meter (the factory caches it by name+version), so the
        // pool.name tag is what separates each pool's time series; identity is not in the name.
        meter = meterFactory.Create(new MeterOptions(PoolMeter.Name) { Version = MeterVersion });

        leaseExceptionCounter = meter.CreateCounter<long>(
            name: "pool.lease.exceptions",
            unit: "{exception}",
            description: "Number of exceptions thrown during pool item lease");

        preparationExceptionCounter = meter.CreateCounter<long>(
            name: "pool.preparation.exceptions",
            unit: "{exception}",
            description: "Number of exceptions thrown during pool item preparation");

        leaseWaitTimeHistogram = meter.CreateHistogram<double>(
            name: "pool.lease.wait.duration",
            unit: "s",
            description: "Time spent waiting for item lease",
            advice: DurationBucketsAdvice);

        preparationTimeHistogram = meter.CreateHistogram<double>(
            name: "pool.item.preparation.duration",
            unit: "s",
            description: "Time spent preparing pool items",
            advice: DurationBucketsAdvice);
    }

    /// <inheritdoc/>
    public void RecordLeaseWaitTime(TimeSpan duration) =>
        leaseWaitTimeHistogram.Record(duration.TotalSeconds, poolNameTag);

    /// <inheritdoc/>
    public void RecordPreparationTime(TimeSpan duration) =>
        preparationTimeHistogram.Record(duration.TotalSeconds, poolNameTag);

    /// <inheritdoc/>
    public IDisposable RegisterItemsAllocatedObserver(Func<int> observeValue)
    {
        var registration = new ObserverRegistration<int>(observeValue, poolNameTag);
        registration.Instrument = meter.CreateObservableUpDownCounter(
            name: "pool.items.allocated",
            observeValues: registration.Observe,
            unit: "{item}",
            description: "Number of items currently allocated");
        return registration;
    }

    /// <inheritdoc/>
    public IDisposable RegisterItemsAvailableObserver(Func<int> observeValue)
    {
        var registration = new ObserverRegistration<int>(observeValue, poolNameTag);
        registration.Instrument = meter.CreateObservableUpDownCounter(
            name: "pool.items.available",
            observeValues: registration.Observe,
            unit: "{item}",
            description: "Number of items currently available");
        return registration;
    }

    /// <inheritdoc/>
    public IDisposable RegisterActiveLeasesObserver(Func<int> observeValue)
    {
        var registration = new ObserverRegistration<int>(observeValue, poolNameTag);
        registration.Instrument = meter.CreateObservableUpDownCounter(
            name: "pool.leases.active",
            observeValues: registration.Observe,
            unit: "{lease}",
            description: "Number of active leases");
        return registration;
    }

    /// <inheritdoc/>
    public IDisposable RegisterQueuedLeasesObserver(Func<int> observeValue)
    {
        var registration = new ObserverRegistration<int>(observeValue, poolNameTag);
        registration.Instrument = meter.CreateObservableUpDownCounter(
            name: "pool.leases.queued",
            observeValues: registration.Observe,
            unit: "{lease}",
            description: "Number of queued leases");
        return registration;
    }

    /// <inheritdoc/>
    public IDisposable RegisterUtilizationRateObserver(Func<double> observeValue)
    {
        var registration = new ObserverRegistration<double>(observeValue, poolNameTag);
        registration.Instrument = meter.CreateObservableGauge(
            name: "pool.utilization",
            observeValues: registration.Observe,
            unit: "1",
            description: "Pool utilization rate (active/total)");
        return registration;
    }

    /// <inheritdoc/>
    public void RecordLeaseException(Exception ex)
    {
        leaseExceptionCounter.Add(1, poolNameTag, ErrorTypeTag(ex));
        logger.LogError(ex, "An exception occurred while leasing a pool item.");
    }

    /// <inheritdoc/>
    public void RecordPreparationException(Exception ex)
    {
        preparationExceptionCounter.Add(1, poolNameTag, ErrorTypeTag(ex));
        logger.LogError(ex, "An exception occurred while preparing a pool item.");
    }

    // OTEL semantic convention: error.type carries the fully-qualified exception type so failures
    // can be sliced by cause via a tag instead of minting a separate counter per exception type.
    private static KeyValuePair<string, object?> ErrorTypeTag(Exception ex) =>
        new("error.type", ex.GetType().FullName);

    // A severable observer registration. Instruments can't be removed from a meter, so instead of
    // disposing the instrument (not possible — instruments are not IDisposable) we drop the value
    // callback on Dispose. After that the observation yields nothing (the instrument stops reporting)
    // and the pool the callback closed over is no longer referenced, so it can be collected. The
    // registration also roots the instrument so it isn't GC'd while the pool is alive.
    private sealed class ObserverRegistration<T>(Func<T> observeValue, KeyValuePair<string, object?> tag)
        : IDisposable
        where T : struct
    {
        private Func<T>? observeValue = observeValue;

        // rooting reference only; instruments are not individually disposable
        public Instrument? Instrument { get; set; }

        public IEnumerable<Measurement<T>> Observe()
        {
            // read once: Dispose may null the field concurrently with a collection pass
            var snapshot = observeValue;
            if (snapshot is not null)
            {
                yield return new Measurement<T>(snapshot(), tag);
            }
        }

        public void Dispose() => observeValue = null;
    }
}
