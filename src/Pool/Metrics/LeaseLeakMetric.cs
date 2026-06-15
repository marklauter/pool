using System.Diagnostics.Metrics;

namespace Pool.Metrics;

/// <summary>
/// Counts leased items that were garbage-collected without being returned to their pool — i.e. a
/// caller leaked a <see cref="Lease{TPoolItem}"/> by never disposing it.
/// <para>
/// Leaks are detected in a finalizer, which has no access to DI or <see cref="IMeterFactory"/>, so
/// the counter lives on a process-lifetime static meter named <see cref="PoolMeter.Name"/>. An
/// OpenTelemetry pipeline that already calls <c>AddMeter(PoolMeter.Name)</c> collects it with no
/// extra wiring; the <c>pool.name</c> tag identifies the leaking pool.
/// </para>
/// </summary>
internal static class LeaseLeakMetric
{
    // process-lifetime meter: leaks surface from finalizers, where no IMeterFactory exists, and the
    // meter must outlive every pool. it is intentionally never disposed.
    private static readonly Meter Meter = new(new MeterOptions(PoolMeter.Name)
    {
        Version = typeof(LeaseLeakMetric).Assembly.GetName().Version?.ToString(),
    });

    internal static readonly Counter<long> Leaked = Meter.CreateCounter<long>(
        name: "pool.leases.leaked",
        unit: "{lease}",
        description: "Number of leases garbage-collected without being returned to the pool");

    internal static void Record(string poolName) =>
        Leaked.Add(1, new KeyValuePair<string, object?>("pool.name", poolName));
}
