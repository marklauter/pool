[![.NET Tests](https://github.com/marklauter/pool/actions/workflows/dotnet.tests.yml/badge.svg)](https://github.com/marklauter/pool/actions/workflows/dotnet.tests.yml)
[![.NET Publish](https://github.com/marklauter/pool/actions/workflows/dotnet.publish.yml/badge.svg)](https://github.com/marklauter/pool/actions/workflows/dotnet.publish.yml)
[![NuGet](https://img.shields.io/nuget/v/MSL.Pool?logo=nuget)](https://www.nuget.org/packages/MSL.Pool/)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0/)

![Pool](https://raw.githubusercontent.com/marklauter/pool/main/images/pool.png "Pool")
![MSL Armory](https://raw.githubusercontent.com/marklauter/pool/main/images/msl.armory.small.png "MSL Armory")

# Pool

*Another weapon from the MSL Armory*

`IPool<TPoolItem>` is a thread-safe object pool for expensive-to-create, reusable instances — connections, clients, sockets. It hands items out under a lease, takes them back on release, and caps how many exist at once.

## Table of contents
- [Pool](#pool)
  - [Table of contents](#table-of-contents)
  - [When to reach for Pool](#when-to-reach-for-pool)
  - [Installation](#installation)
  - [Hello, World](#hello-world)
  - [How leasing works](#how-leasing-works)
  - [Leasing and releasing](#leasing-and-releasing)
    - [Scoped leases](#scoped-leases)
  - [Footguns](#footguns)
    - [Release exactly once](#release-exactly-once)
    - [Never forget to release](#never-forget-to-release)
    - [Dispose reclaims idle items only](#dispose-reclaims-idle-items-only)
    - [Preparation failure discards the item](#preparation-failure-discards-the-item)
  - [Configuration](#configuration)
  - [Item factory](#item-factory)
  - [Preparation strategy](#preparation-strategy)
  - [Dependency injection](#dependency-injection)
  - [Named pools](#named-pools)
  - [Unbounded pool](#unbounded-pool)
  - [Metrics](#metrics)
  - [FAQ](#faq)

## When to reach for Pool

Reach for Pool when an object is expensive to create and safe to reuse, and you want a hard ceiling on how many exist at once:

- Connection pools — SMTP, database, or any long-lived network connection
- Clients that hold a socket or session and cost real time to construct
- Bounded concurrency — `MaxSize` caps concurrent leases, so the pool doubles as a throttle
- Items that idle out — a connection the server drops after inactivity, re-checked on each lease

Skip Pool when the object is cheap to construct — allocate it directly. For stateless reset-and-reuse objects with no async readiness step, [`Microsoft.Extensions.ObjectPool`](https://learn.microsoft.com/aspnet/core/performance/objectpool) is lighter. For `HttpClient`, use `IHttpClientFactory`.

## Installation

```bash
dotnet add package MSL.Pool
```

Pool targets .NET 10.

## Hello, World

Register a pool, then lease and release items through `IPool<TPoolItem>`:

```csharp
using Pool;

services.AddTransient<SmtpConnection>();   // the default factory resolves items from DI
services.AddPool<SmtpConnection>(configuration, options =>
{
    options.MinSize = 2;                   // pre-create two on startup
    options.MaxSize = 10;                  // never more than ten at once
    options.UseDefaultFactory = true;      // construct items from the service provider
});
```

Inject the pool and lease under a `try`/`finally`:

```csharp
public sealed class Mailer(IPool<SmtpConnection> pool)
{
    public async Task SendAsync(Message message)
    {
        var connection = await pool.LeaseAsync();
        try
        {
            await connection.SendAsync(message);
        }
        finally
        {
            pool.Release(connection);      // always release, exactly once
        }
    }
}
```

That's the whole contract: `LeaseAsync` to borrow, `Release` to return. The `finally` returns the item even when the work throws.

## How leasing works

A pool holds a queue of idle items and a capacity gate — a `SemaphoreSlim` with one permit per unit of `MaxSize`. A lease takes a permit; a release returns one. Permits held equals items leased, so the leased count never exceeds `MaxSize`.

Every lease runs the same path:

```text
LeaseAsync
   │
   ├─ permit free? ──no──► wait for a release (up to LeaseTimeout) ──elapsed──► TaskCanceledException
   │       │ yes
   ▼       ▼
 take a permit
   │
   ├─ idle item queued? ──yes──► reuse it (discard first if past IdleTimeout)
   │                    ──no───► create one with the item factory
   ▼
 prepare the item (readiness check, if a strategy is registered)
   │
   ├─ ready ─────► hand it to the caller
   └─ not ready ─► prepare; on failure dispose the item, release the permit, throw
```

When every permit is held, further leases wait in the semaphore's own queue until an item is released or `LeaseTimeout` elapses. That wait queue *is* the backlog — there's no separate waiter list to fall out of sync.

## Leasing and releasing

`IPool<TPoolItem>` is the surface you inject — three methods and a few counters:

- `LeaseAsync()` / `LeaseAsync(CancellationToken)` — borrow an item as a `ValueTask<TPoolItem>`. Reuses an idle item, creates one if the pool is below `MaxSize`, or waits for a release.
- `Release(item)` — return a leased item. Synchronous and `void`. If a lease is waiting, the item goes to it; otherwise it becomes idle.
- `Clear()` — dispose every idle item and refill to `MinSize`. Leased items are untouched and re-enter on release.

Counters for diagnostics and metrics: `ItemsAllocated`, `ItemsAvailable`, `ActiveLeases`, `QueuedLeases`, and `Name`.

`Release` is synchronous by design — returning an item is a queue enqueue and a permit release, neither of which awaits. Wrap every lease in `try`/`finally`:

```csharp
var item = await pool.LeaseAsync(cancellationToken);
try
{
    // use the item
}
finally
{
    pool.Release(item);
}
```

`LeaseAsync(cancellationToken)` cancels the wait. A canceled caller token throws `OperationCanceledException`; a lease that exceeds `LeaseTimeout` throws `TaskCanceledException`.

### Scoped leases

`LeaseScopeAsync` wraps the lease in a disposable `Lease<TPoolItem>`, so a `using` returns the item on scope exit — no `try`/`finally`, and a double dispose is a safe no-op:

```csharp
using var lease = await pool.LeaseScopeAsync(cancellationToken);
await lease.Item.SendAsync(message, cancellationToken);
```

This is the recommended way to lease: it turns "forgot to release" — which no analyzer can see — into "forgot to dispose," which `using` and the IDisposable analyzers already guard against. It also makes the release idempotent, closing the double-release footgun below. The raw `LeaseAsync`/`Release` pair stays available for advanced cases. A `Lease` that is garbage-collected without being disposed is counted on the `pool.leases.leaked` counter, so leaks are observable even when they slip through.

## Footguns

The lease/release contract is a balanced pair, and the pool trusts you to honor it. Like `ArrayPool<T>.Return`, it doesn't police misuse — these are the sharp edges.

### Release exactly once

The pool tracks no ownership. Releasing the same item twice — or releasing an item the pool never handed you — enqueues a duplicate, after which two callers can lease the *same* instance and corrupt each other's work. A double release also over-counts the capacity gate, letting the pool create past `MaxSize`, or throws `SemaphoreFullException` when the gate is already full. Release each leased item once, from a `finally`.

### Never forget to release

A lease that never returns holds its permit for the life of the pool. Leak `MaxSize` permits and the pool saturates: every later lease waits out the full `LeaseTimeout` and then throws. The `try`/`finally` is not optional — or use a [scoped lease](#scoped-leases) so the return is automatic.

### Dispose reclaims idle items only

Disposing the pool disposes the items sitting idle in it. Items leased out at that moment belong to the caller — the pool can't reclaim them, so dispose them yourself. Releasing an item to a disposed pool throws `ObjectDisposedException`, as does leasing from one.

### Preparation failure discards the item

When a registered preparation strategy throws, the pool disposes that item rather than recirculate a broken one, releases the permit, and rethrows. Catch the exception at the call site and retry the lease for a fresh item.

## Configuration

`PoolOptions` sets sizing, timeouts, and which defaults to register. Bind it from the `"PoolOptions"` configuration section, pass an `Action<PoolOptions>`, or both — the action runs after binding:

```csharp
services.AddPool<SmtpConnection>(configuration, options =>
{
    options.MinSize = 2;
    options.MaxSize = 10;
    options.LeaseTimeout = TimeSpan.FromSeconds(30);
});
```

Options and their defaults:

- `MinSize` — items created up front, and the floor `Clear()` refills to. Defaults to `0`.
- `MaxSize` — hard cap on items in existence, and therefore on concurrent leases. At least `1`. Defaults to `100`.
- `LeaseTimeout` — how long a lease waits for an item before throwing `TaskCanceledException`. Defaults to `Timeout.InfiniteTimeSpan` (wait forever).
- `PreparationTimeout` — how long the readiness check and preparation may take. Defaults to `Timeout.InfiniteTimeSpan`.
- `IdleTimeout` — how long an item may sit idle before the next lease discards and disposes it instead of reusing it. Eviction is lazy: it happens when a lease meets the stale item, not on a background timer. Defaults to `Timeout.InfiniteTimeSpan` (never expire).
- `UseDefaultFactory` — register the default item factory, which resolves `TPoolItem` from the service provider. Defaults to `false`.
- `UseDefaultPreparationStrategy` — register the default preparation strategy, which reports every item ready. Defaults to `false`.

## Item factory

The pool creates new items through `IItemFactory<TPoolItem>`:

```csharp
public interface IItemFactory<TPoolItem>
    where TPoolItem : class
{
    TPoolItem CreateItem();
}
```

Supply one two ways:

- Set `UseDefaultFactory = true` to use the built-in factory. It resolves `TPoolItem` from the service provider, so register the item type as well: `services.AddTransient<SmtpConnection>()`.
- Implement `IItemFactory<TPoolItem>` and register it with `AddPoolItemFactory<TPoolItem, TFactory>()` for construction the container can't express — credentials, endpoints, handcrafted state.

## Preparation strategy

A pooled item can go stale between leases — an SMTP server drops an idle connection, a token expires. `IPreparationStrategy<TPoolItem>` checks an item on lease and restores it when needed:

```csharp
public interface IPreparationStrategy<TPoolItem>
    where TPoolItem : class
{
    ValueTask<bool> IsReadyAsync(TPoolItem item, CancellationToken cancellationToken);
    Task PrepareAsync(TPoolItem item, CancellationToken cancellationToken);
}
```

On each lease the pool calls `IsReadyAsync`; if it returns `false`, the pool calls `PrepareAsync` before handing the item over, bounded by `PreparationTimeout`. Register a strategy with `AddPreparationStrategy<TPoolItem, TStrategy>()`, or set `UseDefaultPreparationStrategy = true` for the built-in one that reports every item ready.

An SMTP readiness check and preparation over `MailKit.IMailTransport`:

```csharp
public async ValueTask<bool> IsReadyAsync(IMailTransport item, CancellationToken cancellationToken) =>
    item.IsConnected
    && item.IsAuthenticated
    && await NoOpAsync(item, cancellationToken);

public async Task PrepareAsync(IMailTransport item, CancellationToken cancellationToken)
{
    await item.ConnectAsync(host.Name, host.Port, host.UseSsl, cancellationToken);
    await item.AuthenticateAsync(credentials.UserName, credentials.Password, cancellationToken);
}
```

If `PrepareAsync` throws, the pool disposes the item and rethrows — retry the lease for a fresh one.

## Dependency injection

Every registration extension lives in the `Pool` namespace and registers singletons:

- `AddPool<TPoolItem>(configuration, configureOptions)` — the pool, its options, and metrics. Also registers the default factory and preparation strategy when `UseDefaultFactory` / `UseDefaultPreparationStrategy` are set.
- `AddPoolItemFactory<TPoolItem, TFactory>()` — a custom `IItemFactory<TPoolItem>`.
- `AddPreparationStrategy<TPoolItem, TStrategy>()` — a custom `IPreparationStrategy<TPoolItem>`.
- `AddDefaultPoolMetrics<TPoolItem>()` — the default metrics, when you build a pool without `AddPool`.

A pool with a custom factory and strategy:

```csharp
services
    .AddPoolItemFactory<SmtpConnection, SmtpConnectionFactory>()
    .AddPreparationStrategy<SmtpConnection, SmtpConnectionPreparationStrategy>()
    .AddPool<SmtpConnection>(configuration, options =>
    {
        options.MinSize = 2;
        options.MaxSize = 10;
    });
```

## Named pools

Run several independently configured pools for one item type — a small read pool and a large write pool of the same connection. Register the pool factory once, then a named pool per configuration:

```csharp
services.AddPoolFactory<DbConnection>();

services.AddNamedPool<DbConnection>("ReadPool", configuration, options =>
{
    options.MinSize = 5;
    options.MaxSize = 20;
});

services.AddNamedPool<DbConnection>("WritePool", configuration, options =>
{
    options.MinSize = 2;
    options.MaxSize = 10;
});
```

A named pool's key is `"{name}.{typeof(TPoolItem).Name}.Pool"`. Build it with `ServiceKey.Create<TPoolItem>(name)` rather than hand-formatting, then resolve through `IPoolFactory<TPoolItem>`:

```csharp
public sealed class Repository(IPoolFactory<DbConnection> pools)
{
    private readonly IPool<DbConnection> readPool =
        pools.CreatePool(ServiceKey.Create<DbConnection>("ReadPool"));
    private readonly IPool<DbConnection> writePool =
        pools.CreatePool(ServiceKey.Create<DbConnection>("WritePool"));
}
```

Each named pool also reads an optional per-pool section, `"{key}_PoolOptions"`, layered over the shared `"PoolOptions"` section.

To bind a pool to a dedicated client type, register the client and its pool together. `AddPool<TPoolItem, TClient>` keys the pool by the client type and injects the `IPool<TPoolItem>` into the client's constructor:

```csharp
services.AddPool<DbConnection, ReadClient>(configuration,
    options =>
    {
        options.MinSize = 5;
        options.MaxSize = 20;
    },
    client =>
    {
        // configure the resolved client
    });
```

## Unbounded pool

`UnboundedPool<TPoolItem>` is a variant that never blocks a lease. Where the default pool *borrows* — it holds a hard `MaxSize` ceiling and a lease waits when every item is out — the unbounded pool *transfers ownership*: `LeaseAsync` returns immediately, handing back an idle item or creating one on the spot, and `Release` becomes an optional optimization that donates an item back for reuse. It's modeled on [`System.Buffers.ArrayPool<T>`](https://learn.microsoft.com/dotnet/api/system.buffers.arraypool-1): rent freely, return when you can, and a return you skip costs reuse but never correctness.

Because a lease transfers ownership, the contract is looser than the bounded pool's:

- **Release is optional.** A leased item the caller never releases is not reused. When the item is `IDisposable`, disposing it is the caller's responsibility — as it is for any object the caller owns.
- **There is no `MaxSize` and no `LeaseTimeout`.** A lease never waits for capacity, so there is nothing to bound or time out. The only cap is on *retention*: `MaxIdle` bounds how many returned items the pool keeps for reuse. Returns past that cap are dropped, and disposed when the item is `IDisposable`.
- **The pool disposes only the idle items it still holds** — on a capped return that overflows, on idle-timeout eviction, on `Clear`, and on `Dispose`. Items out on lease belong to their callers.

Reach for the unbounded pool when you want pooled reuse without a concurrency ceiling: when blocking or timing out a caller costs more than allocating one more item, and you want to cap memory by how many idle items you retain rather than by how many can be live at once.

### Registration

`AddUnboundedPool` parallels `AddPool`, binding `UnboundedPoolOptions` from the `"UnboundedPoolOptions"` configuration section:

```csharp
services.AddTransient<SmtpConnection>();
services.AddUnboundedPool<SmtpConnection>(configuration, options =>
{
    options.MinSize = 2;               // pre-create two on startup
    options.MaxIdle = 50;              // retain up to fifty idle items for reuse
    options.UseDefaultFactory = true;  // construct items from the service provider
});
```

Inject the same `IPool<SmtpConnection>` surface — the concrete pool type is an implementation detail:

```csharp
var connection = await pool.LeaseAsync();
try
{
    await connection.SendAsync(message);
}
finally
{
    pool.Release(connection);          // optional here, but still returns the item for reuse
}
```

Scoped leases ([`LeaseScopeAsync`](#scoped-leases)) work unchanged and remain the cleanest way to lease — the dispose donates the item back for reuse. Named pools and typed clients have unbounded counterparts as well: `AddNamedUnboundedPool<TPoolItem>(name, …)` and `AddUnboundedPool<TPoolItem, TClient>(…)`, matching the bounded [named pool](#named-pools) registrations.

### Options

`UnboundedPoolOptions` carries only the settings that apply when a lease never blocks:

- `MinSize` — items created up front, and the floor `Clear()` refills to. Capped by `MaxIdle` when seeding. Defaults to `0`.
- `MaxIdle` — the maximum number of idle items retained for reuse. Returns beyond this cap are dropped, and disposed when the item is `IDisposable`. Zero retains nothing — every return is dropped, for pure allocate-on-lease. Defaults to `100`.
- `IdleTimeout` — how long an item may sit idle before the next lease discards and disposes it instead of reusing it. Eviction is lazy, as in the bounded pool. Defaults to `Timeout.InfiniteTimeSpan`.
- `PreparationTimeout` — how long the readiness check and preparation may take, combined. Defaults to `Timeout.InfiniteTimeSpan`.
- `UseDefaultFactory` and `UseDefaultPreparationStrategy` — register the built-in factory and preparation strategy, as for the bounded pool.

`UnboundedPoolOptions` has no `MaxSize` or `LeaseTimeout`, because a lease never waits.

### Metrics

The unbounded pool reports through the same `IPoolMetrics` instruments under the `"MSL.Pool"` meter, tagged with its own `pool.name` of the form `"{typeof(TPoolItem).Name}.UnboundedPool"`. Two instruments read differently by design: `pool.leases.queued` is always `0`, because a lease never queues, and `pool.items.allocated` is `ActiveLeases + ItemsAvailable` — a gauge that counts items a caller leased and never returned, because those items are still in use.

## Metrics

> **Upgrading from 7.0?** The meter, instrument names, and tags changed in 7.1. See [UPGRADING](UPGRADING.md) for the before/after mapping and migration steps.

`AddPool` wires up `IPoolMetrics`, implemented by `DefaultPoolMetrics` over `System.Diagnostics.Metrics` — the API OpenTelemetry consumes directly. Every pool publishes under one stable meter, `PoolMeter.Name` (`"MSL.Pool"`), and carries its identity as a `pool.name` tag rather than in the instrument name. The instruments:

- `pool.lease.exceptions`, `pool.preparation.exceptions` — counters of failed leases and failed preparations, tagged `error.type` with the exception type
- `pool.lease.wait.duration`, `pool.item.preparation.duration` — histograms in seconds (OTEL convention), with bucket boundaries tuned for sub-millisecond-to-seconds pool latencies
- `pool.items.allocated`, `pool.items.available`, `pool.leases.active`, `pool.leases.queued` — observable up/down counters of pool state
- `pool.utilization` — observable gauge of active leases over allocated items
- `pool.leases.leaked` — counter of leases garbage-collected without being returned (see [Scoped leases](#scoped-leases))

Every measurement carries a `pool.name` tag, so metrics aggregate across pools and you slice by pool as a dimension. Collect them with any `System.Diagnostics.Metrics` listener — OpenTelemetry, `dotnet-counters`, a custom exporter — with a single `AddMeter`:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(PoolMeter.Name)
        .AddPrometheusExporter());
```

That one subscription captures every pool in the process, named or not. Distinguish instances by the `pool.name` tag — for example, `ReadPool` and `WritePool` over `DbConnection` both report under `MSL.Pool`, separated by their tag values rather than by separate meters.

The library takes no dependency on the OpenTelemetry SDK; it only exposes the meter via `System.Diagnostics.Metrics`, leaving the choice of exporter to the host app.

To replace the default, implement `IPoolMetrics` and register it before `AddPool` resolves its own:

```csharp
services.AddSingleton<IPoolMetrics, MyPoolMetrics>();
```

## FAQ

**Why is `Release` synchronous when `LeaseAsync` is async?**
Returning an item enqueues it and releases a permit — no I/O, nothing to await. Leasing may wait for a free permit or run an async readiness check, so it's the async half.

**What happens if I forget to release?**
The permit stays held for the life of the pool. Leak `MaxSize` of them and every later lease waits out `LeaseTimeout` and throws. Always release from a `finally`.

**Can the pool exceed `MaxSize`?**
Not under correct use — the capacity gate guarantees it. A double release breaks that guarantee by over-counting the gate; see [Footguns](#footguns).

**How do idle items get cleaned up?**
Lazily. An item past `IdleTimeout` is disposed the next time a lease meets it in the queue, not on a background timer. `Clear()` disposes all idle items on demand.

**Is the pool thread-safe?**
Yes. Concurrent leases and releases are safe — the idle queue is a `ConcurrentQueue` and capacity is a `SemaphoreSlim`.

**Which exception signals a lease timeout?**
`TaskCanceledException` for an elapsed `LeaseTimeout`; `OperationCanceledException` for a canceled caller token.
