## Build Status
[![.NET Test](https://github.com/marklauter/pool/actions/workflows/dotnet.tests.yml/badge.svg)](https://github.com/marklauter/pool/actions/workflows/dotnet.tests.yml)
[![.NET Publish](https://github.com/marklauter/pool/actions/workflows/dotnet.publish.yml/badge.svg)](https://github.com/marklauter/pool/actions/workflows/dotnet.publish.yml)
[![Nuget](https://img.shields.io/badge/Nuget-v5.0.0-blue)](https://www.nuget.org/packages/MSL.Pool/)
[![Nuget](https://img.shields.io/badge/.NET-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0/)

## 
![Pool Logo](https://raw.githubusercontent.com/marklauter/pool/main/images/pool.png "Pool Logo")

# Pool
`IPool<TPoolItem>` is an object pool that uses the lease/release pattern.
It allows for, but does not require, [custom preparation strategies](#pool-item-preparation-strategy).
Common use cases for [preparation strategies](#pool-item-preparation-strategy) 
include objects that benefit from long-lived connections, 
like SMTP or database connections.

## Github Repository
[https://github.com/marklauter/pool](https://github.com/marklauter/pool)

## Nuget Package
### Nuget.org Page
[https://www.nuget.org/packages/MSL.Pool](https://www.nuget.org/packages/MSL.Pool)
### Package Install
```console
dotnet add package MSL.Pool
```

## The Lease / Release Pattern
When the pool is instantiated, it creates a minimum number of poolable items and places them in the available items queue.

When a lease is requested, the pool attempts to dequeue an item. 
If an item is returned from the queue, the item is returned on a task.
However, if the item queue is empty, the pool attempts to create a new item to fulfill the lease request.
If the pool has reached its allocation limit, it enqueues a new lease request object into the lease request queue.
Then it returns the least request's `TaskCompletionSource.Task` to the caller.

The caller will block on await until an item is released back to the pool or until the lease request times out. 
First, the release operation attempts to dequeue an active lease request.
If a lease request is returned, the least request's task is completed with the item being released by the caller.
But if the lease request queue is empty, the item will be placed in the available item queue.

## Pool
`IPool<TPoolItem>` is implmented _internally_ by `Pool<TPoolItem>`.

You can access `IPool<TPoolItem>` by registering it with the service collection by calling 
one of the `IServiceCollection` extensions from the `Pool.DependencyInjection` namespace.
See [Dependency Injection](#dependency-injection) for more information.

`IPool<TPoolItem>` provides three methods with convenient overloads:
- `LeaseAsync` - returns an item from the pool and optionally [performs a ready check](#pool-item-preparation-strategy)
- `ReleaseAsync` - returns an item to the pool
- `ClearAsync` - clears the pool, disposes of items as required, and reinitializes the pool with `PoolOptions.MinSize` items

The caller is responsible for calling `ReleaseAsync` when it no longer needs the item.
I recommend using try / finally.
```csharp
var item = await pool.LeaseAsync();
try
{
    item.DoStuff();
}
finally
{
    await pool.ReleaseAsync(item);
}
```

`Pool<TPoolItem>` has three dependencies injected into the constructor:
- `IItemFactory<TPoolItem>`
- `IPreparationStrategy<TPoolItem>`
- `PoolOptions`

See [Dependency Injection](#dependency-injection) for more information.

The pool will use an [item factory](#pool-item-factory) to create new items as required.
During the lease operation, the pool invokes a [ready checker](#pool-item-preparation-strategy) 
to initialize an item that isn't ready.

## Pool Item Factory
Implement the `IItemFactory<TPoolItem>` interface to create new items for the pool. 
The library provides a default pool implementation that uses `IServiceProvider` to construct items.
To use the default implementation, call `AddPool<TPoolItem>` or 
`AddPoolWithDefaultFactory<TPoolItem, TPreparationStrategy>` 
when registering the pool with the service collection.
See [Dependency Injection](#dependency-injection) for more information.

## Pool Item Preparation Strategy
Implement the `IPreparationStrategy<TPoolItem>` interface to ensure an item is ready for use when leased from the pool.

There's a default `IPreparationStrategy<TPoolItem>` implementation that always returns 
`true` from the `IsReadyAsync` method.
To use the default implementation with a custom item factory, 
call `AddPool<TPoolItem>` 
when registering the pool with the service collection.
See [Dependency Injection](#dependency-injection) for more information.

Ready check is useful for items that may become inactive for some time, 
such as an SMTP connection that has been idle long enough for the server to terminate
the connection.

For example, if you're implementing an SMTP connection pool, 
the lease operation can verify the connection to the STMP server 
by invoking the SMTP `no-op`.  You can connect and authenticate to the SMTP server if the ready check fails. 

Sample SMTP connection ready check implementation using `MailKit.IMailTransport`:
```csharp
public async ValueTask<bool> IsReadyAsync(IMailTransport item, CancellationToken cancellationToken) =>
    item.IsConnected
    && item.IsAuthenticated
    && await NoOpAsync(item, cancellationToken);
```

Sample SMTP connection `PrepareAsync` implementation using `MailKit.IMailTransport`:
```csharp
public async Task PrepareAsync(IMailTransport item, CancellationToken cancellationToken)
{
    await item.ConnectAsync(hostOptions.Host, hostOptions.Port, hostOptions.UseSsl, cancellationToken);
    await item.AuthenticateAsync(credentials.UserName, credentials.Password, cancellationToken);
}
```

## Dependency Injection
The `ServiceCollectionExtensions` class is in the `Pool.DependencyInjection` namespace.
- Call `AddPool<TPoolItem>` to register a singleton pool. Pass `Action<PoolRegistrationOptions>` to specify whether or not to register the default item factory and ready check implementations.
- Call `AddPreparationStrategy<TPoolItem, TPreparationStrategy>` to register a singleton preparation strategy.
- Call `AddPoolItemFactory<TPoolItem, TFactoryImplementation>` to register a singleton item factory implementation.

### Sample `AddPool<TPoolItem>` Registration
```csharp
services.AddPool<IMailTransport>(configuration, options =>
{
    // use default factory, which uses the service provider to construct pool items
    options.RegisterDefaultFactory = true;
});
```

## Named Pools

Starting from March 2025, Pool supports creating multiple named instances of pools for the same item type. This allows you to configure different pools with different settings for the same type of item.

### Why Use Named Pools?

Named pools are useful when you need:
- Different pool configurations for the same type (different min/max sizes, timeouts, etc.)
- Dedicated pools for different use cases or components in your application
- Isolating pool resources for different concerns

### Using Named Pools

#### Basic Named Pool Registration

To register a named pool and its factory:

**note: the name provided will be converted to a service key in the format `{name}.{typeof(TPoolItem).Name}.pool`**

```csharp
// Add the pool factory
services.AddPoolFactory<MyPoolItem>();

// Add a named pool
services.AddNamedPool<MyPoolItem>(
    "ReadPool",
    configuration,
    options => 
    {
        options.MinSize = 5;
        options.MaxSize = 20;
        options.LeaseTimeout = TimeSpan.FromSeconds(30);
    });

// Add another named pool with different configuration
services.AddNamedPool<MyPoolItem>(
    "WritePool",
    configuration,
    options => 
    {
        options.MinSize = 2;
        options.MaxSize = 10;
        options.LeaseTimeout = TimeSpan.FromSeconds(60);
    });
```

You can also register a typed client that will use a specific named pool:

```csharp
// Register a pool with a typed client
services.AddPool<MyPoolItem, MyPoolClient>(
    configuration,
    options => 
    {
        options.MinSize = 5;
        options.MaxSize = 20;
    },
    client => 
    {
        // Configure the pool client if needed
    });
```

Inject the IPoolFactory<TPoolItem> into your class and create the pool you need:
```csharp
public class MyService
{
    private readonly IPool<MyPoolItem> readPool;
    private readonly IPool<MyPoolItem> writePool;

    public MyService(IPoolFactory<MyPoolItem> poolFactory)
    {
        readPool = poolFactory.CreatePool("ReadPool.MyPoolItem.pool");
        writePool = poolFactory.CreatePool("WritePool.MyPoolItem.pool");
    }

    public async Task DoReadOperationAsync()
    {
        var item = await readPool.LeaseAsync();
        try
        {
            // Use the item for read operations
        }
        finally
        {
            await readPool.ReleaseAsync(item);
        }
    }

    public async Task DoWriteOperationAsync()
    {
        var item = await writePool.LeaseAsync();
        try
        {
            // Use the item for write operations
        }
        finally
        {
            await writePool.ReleaseAsync(item);
        }
    }
}
```

## Metrics

`IPoolMetrics` provides a comprehensive metrics collection system for your pools, allowing you to monitor performance, diagnose issues, and optimize usage patterns. The Pool library includes a default implementation (`DefaultPoolMetrics`) that integrates with .NET's built-in metrics infrastructure.

Metrics are named using the pattern `{poolName}.{metricName}` and include the following:

### Counter Metrics
- `{name}.lease_exception` - Tracks the number of exceptions thrown during pool item lease operations
- `{name}.preparation_exception` - Tracks the number of exceptions thrown during pool item preparation

### Histogram Metrics
- `{name}.lease_wait_time` - Measures the time spent waiting to acquire a pool item (in milliseconds)
- `{name}.item_preparation_time` - Measures the time spent preparing pool items before use (in milliseconds)

### Observable Metrics
- `{name}.items_allocated` - Tracks the total number of items allocated in the pool
- `{name}.items_available` - Tracks the number of items currently available for lease
- `{name}.active_leases` - Tracks the number of currently active leases
- `{name}.queued_leases` - Tracks the number of lease requests waiting in the queue
- `{name}.utilization_rate` - Monitors the pool utilization rate (active leases / total items)

### Using Pool Metrics

Pool metrics are automatically enabled when you register a pool with the service collection. The metrics can be consumed by any metrics collector that supports .NET's metrics API, such as OpenTelemetry, Prometheus, or custom exporters.

Example of configuring OpenTelemetry to collect pool metrics:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(builder => builder
        // Add your pool metrics to OpenTelemetry
        .AddMeter(Pool<MyPoolItem>.PoolName)
        // Configure exporters as needed
        .AddPrometheusExporter());
```

You can also create a custom metrics implementation by implementing the `IPoolMetrics` interface and registering it with the DI container:

```csharp
services.AddPool<MyPoolItem>(configuration, options =>
{
    options.RegisterDefaultFactory = true;
})
.AddSingleton<IPoolMetrics, MyCustomPoolMetrics>();
```

### Using Metrics with Named Pools

When working with named pools, each pool instance will have its own set of metrics with the name pattern `{poolName}.{poolItemType.Name}.Pool`. To collect metrics from named pools, you'll need to ensure you're adding the correct meter name to your metrics system.

Example of configuring OpenTelemetry to collect metrics from a named pool:

```csharp
// First, register your named pools

services.AddNamedPool<DatabaseConnection>(
    "ReadOnly",
    configuration,
    options => 
    {
        options.MinSize = 5;
        options.MaxSize = 20;
    });

services.AddNamedPool<DatabaseConnection>(
    "ReadWrite",
    configuration,
    options => 
    {
        options.MinSize = 2;
        options.MaxSize = 10;
    });

services.AddOpenTelemetry()
    .WithMetrics(builder => builder
        // Add meters for the named pools
        .AddMeter($"ReadOnly.{Pool<DatabaseConnection>.PoolName}")
        .AddMeter($"ReadWrite.{Pool<DatabaseConnection>.PoolName}")
        // Configure exporters as needed
        .AddPrometheusExporter());
```

With this configuration, your metrics system will collect separate metrics for each named pool, allowing you to monitor and analyze the performance of individual pools independently.

Pool metrics can help you answer important questions about your pool's performance and health:
- Is the pool sized appropriately for my workload?
- Are items taking too long to prepare?
- Are callers waiting too long to acquire items?
- Is the pool under heavy load or running efficiently?

Using these metrics, you can fine-tune your pool configuration for optimal performance in your specific scenarios.

## Dev Log
- 12 FEB 2024 - started SMTP pool at the end of 2023, but got busy with other stuff. I'll take it up again soon though because I need it for a work project.
- 05 MAY 2024 - prepping the library for Nuget by supporting dotnet 6, 7 and 8.
- 06 MAY 2024 - published to Nuget.
- 17 MAY 2024 - added tests for out-of-order disposal scenarios.
- 17 MAY 2024 - updated `readme.md`
- 17 MAY 2024 - `Sample/Smtp.Pool` is still a work in progress.
- 18 MAY 2024 :ALERT: breaking changes.
- 18 MAY 2024 - refactored dependency injection extensions. 
- 18 MAY 2024 - refactored to use ValueTask on LeaseAsync method. 
- 16 JUL 2024 - better naming and cleaned up smtp sample project.
- 17 JUL 2024 - added idle timeout
- XX MAR 2025 - better lease timout handling
- XX MAR 2025 - added metrics
- 16 MAR 2025 - added named pools
