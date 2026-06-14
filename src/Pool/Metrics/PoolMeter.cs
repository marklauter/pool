namespace Pool.Metrics;

/// <summary>
/// Identifies the single <see cref="System.Diagnostics.Metrics.Meter"/> that every pool publishes
/// its instruments under. Subscribe an OpenTelemetry (or any <c>System.Diagnostics.Metrics</c>)
/// pipeline to this one name to collect metrics from every pool in the process:
/// <code>
/// services.AddOpenTelemetry()
///     .WithMetrics(metrics => metrics
///         .AddMeter(PoolMeter.Name)
///         .AddPrometheusExporter());
/// </code>
/// Individual pools are distinguished by the <c>pool.name</c> tag on every measurement,
/// not by separate meter names — so a single <c>AddMeter</c> call captures them all.
/// </summary>
public static class PoolMeter
{
    /// <summary>
    /// The shared meter name for all pools: <c>"MSL.Pool"</c>.
    /// </summary>
    public const string Name = "MSL.Pool";
}
