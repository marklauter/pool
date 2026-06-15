# Upgrading

## 7.0 → 7.1

**Version 7.1 changes the metrics telemetry contract.** The pool now publishes under a single, stable meter and follows OpenTelemetry naming conventions: instrument names no longer embed the pool's identity, and each pool is distinguished by a `pool.name` tag instead.

> Note: these are breaking changes, and they shipped in the 7.1.0 minor release. Under semantic versioning they warranted a major bump; that was a mistake on our part. If you need to avoid them, pin to `7.0.*`.

### Do I need to change my code?

**Almost certainly not — unless you implement `IPoolMetrics` yourself.**

- `IPool<TPoolItem>`, `Pool<TPoolItem>`, and the `AddPool` / `AddNamedPool` / `AddPoolFactory` registrations are unchanged.
- **If you implement `IPoolMetrics`**, it changed: the five `Register*Observer` methods now return `IDisposable` instead of `void`, so existing implementations need updating before they compile. See [Custom `IPoolMetrics` implementations](#custom-ipoolmetrics-implementations).

The break is in the **telemetry you export** — meter name, instrument names, and tags. The work is in your observability configuration: the `AddMeter` call, dashboards, alerts, and queries.

### What changed

| | 7.0 | 7.1 |
|---|---|---|
| Meter name | `Pool<T>.PoolName` — `"{T.Name}.Pool"` (named pools: `"{name}.{T.Name}.Pool"`) | `PoolMeter.Name` — `"MSL.Pool"` for **every** pool |
| Pool identity | embedded in the meter and instrument names | carried as a `pool.name` tag |
| Meter version | none | stamped with the library version |

Instrument renames (every instrument also gains a `pool.name` tag):

| 7.0 instrument | 7.1 instrument | Added tags |
|---|---|---|
| `{T.Name}.Pool.lease_exception` | `pool.lease.exceptions` | `pool.name`, `error.type` |
| `{T.Name}.Pool.preparation_exception` | `pool.preparation.exceptions` | `pool.name`, `error.type` |
| `{T.Name}.Pool.lease_wait_time` | `pool.lease.wait.duration` | `pool.name` |
| `{T.Name}.Pool.item_preparation_time` | `pool.item.preparation.duration` | `pool.name` |
| `{T.Name}.Pool.items_allocated` | `pool.items.allocated` | `pool.name` |
| `{T.Name}.Pool.items_available` | `pool.items.available` | `pool.name` |
| `{T.Name}.Pool.active_leases` | `pool.leases.active` | `pool.name` |
| `{T.Name}.Pool.queued_leases` | `pool.leases.queued` | `pool.name` |
| `{T.Name}.Pool.utilization_rate` | `pool.utilization` | `pool.name` |

**Durations are now reported in seconds, not milliseconds** (the OpenTelemetry convention). The duration histograms also ship recommended bucket boundaries (via instrument advice) spanning sub-millisecond to ten seconds, so the SDK's millisecond-tuned default buckets don't collapse every value into one bucket. If you set explicit histogram views or alert thresholds against the old millisecond values, divide by 1000. The two exception counters now also carry `error.type` (the fully-qualified exception type) so you can slice failures by cause. Unit annotations moved to [UCUM](https://ucum.org/) form — `s`, `{item}`, `{lease}`, `{exception}`, `1` — which some exporters surface in the exported name.

### Step 1 — Update the meter subscription

The dynamic per-pool meter names are gone. Subscribe once to `PoolMeter.Name`; it captures every pool in the process, named or not.

```diff
  services.AddOpenTelemetry()
      .WithMetrics(metrics => metrics
-         .AddMeter(Pool<MyPoolItem>.PoolName)
+         .AddMeter(PoolMeter.Name)
          .AddPrometheusExporter());
```

`PoolMeter` lives in the `Pool.Metrics` namespace:

```csharp
using Pool.Metrics;
```

### Step 2 — Collapse named-pool subscriptions

In 7.0 each named pool needed its own `AddMeter`. In 7.1 they all report under `MSL.Pool`, separated by their `pool.name` tag value (e.g. `"ReadPool.DbConnection.Pool"`).

```diff
  services.AddOpenTelemetry()
      .WithMetrics(metrics => metrics
-         .AddMeter($"ReadPool.{Pool<DbConnection>.PoolName}")
-         .AddMeter($"WritePool.{Pool<DbConnection>.PoolName}")
+         .AddMeter(PoolMeter.Name)
          .AddPrometheusExporter());
```

### Step 3 — Update dashboards, alerts, and queries

This is the part that won't fail a build but will silently break panels and alerts:

- **Rename the series** to the 7.1 instrument names above.
- **Move pool identity from the metric name to the `pool.name` attribute.** Filters and group-bys that keyed off the old per-pool metric name should now filter/group by the `pool.name` label. The upside: metrics from multiple pools now aggregate directly, which the old name-per-pool scheme prevented.
- **Use `error.type`** to break down `pool.lease.exceptions` / `pool.preparation.exceptions` by exception type, or to alert on a specific failure.
- **Durations are in seconds** (was milliseconds). Update any query, threshold, or axis that assumed milliseconds — divide old values by 1000.
- **Prometheus / OpenMetrics names are sanitized** by the exporter: dots become underscores and a unit suffix may be appended. For example `pool.lease.wait.duration` (unit `s`) typically surfaces as `pool_lease_wait_duration_seconds`. Confirm the exact names against your exporter after upgrading.

### Custom `IPoolMetrics` implementations

The `IPoolMetrics` interface changed — custom implementations need two edits:

- **The five `Register*Observer` methods now return `IDisposable`** instead of `void`. The pool keeps these handles and disposes them when the pool is disposed, which lets your implementation stop the observable instruments from reporting (and release the references they captured) on pool teardown. .NET instruments cannot be removed from a meter, so the default implementation severs the observation callback rather than disposing the instrument. If your implementation does not need teardown behavior, return a no-op disposable:

  ```csharp
  public IDisposable RegisterActiveLeasesObserver(Func<int> observeValue)
  {
      // ... create your observable instrument ...
      return NoopDisposable.Instance; // or a handle that severs/stops the instrument
  }
  ```

- If you embedded the pool name into your instrument names (following the 7.0 docs), consider switching to the 7.1 convention — a stable instrument name plus a `pool.name` tag.

The `name` argument the framework passes to a metrics implementation is now intended as the **tag value**, not a name prefix. If you construct `DefaultPoolMetrics` directly (uncommon — `AddPool` and `AddDefaultPoolMetrics` do this for you), `name` becomes the `pool.name` tag and no longer affects the meter or instrument names.

### Why the change

A single stable meter means one `AddMeter(PoolMeter.Name)` subscribes to every pool instead of enumerating a meter per pool type or per named instance. Moving identity into the `pool.name` tag lets measurements aggregate across pools and keeps metric-name cardinality flat — the idiomatic OpenTelemetry shape, and what lets a generic Grafana dashboard work across all your pools.
