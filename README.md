## Build Status
[![.NET Test](https://github.com/marklauter/pool/actions/workflows/dotnet.tests.yml/badge.svg)](https://github.com/marklauter/pool/actions/workflows/dotnet.tests.yml)
[![.NET Publish](https://github.com/marklauter/pool/actions/workflows/dotnet.publish.yml/badge.svg)](https://github.com/marklauter/pool/actions/workflows/dotnet.publish.yml)
[![Nuget](https://img.shields.io/badge/Nuget-v2.0.0-blue)](https://www.nuget.org/packages/MSL.Pool/)
[![Nuget](https://img.shields.io/badge/.NET-6.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/6.0)
[![Nuget](https://img.shields.io/badge/.NET-7.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/7.0)
[![Nuget](https://img.shields.io/badge/.NET-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0/)

<div>
<img src="https://github.com/marklauter/pool/blob/main/images/pool.svg" title="pool-logo" alt="pool-logo" height="128" />

# Pool
`IPool<TPoolItem>` is an object pool that uses the lease/release pattern.
It allows for but does not require, [ready checks](##pool-item-ready-checker) 
with initialization on ready check failure. 
Common use cases for [ready checks](##pool-item-ready-checker) 
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

The caller will block on await until timing out, or until an item is released back to the pool. 
First, the release operation attempts to dequeue an active lease request.
If a lease request is returned, the least request's task is completed with the item being released by the caller.
But if the lease request queue is empty, the item will be placed in the available item queue.

## Pool
`IPool<TPoolItem>` is implmented _internally_ by `Pool<TPoolItem>`.

You can access `IPool<TPoolItem>` by registering it with the service collection by calling 
one of the `IServiceCollection` extensions from the `Pool.DependencyInjection` namespace.
See [Dependency Injection](##dependency-injection) for more information.

`IPool<TPoolItem>` provides three methods with convenient overloads:
- `LeaseAsync` - returns an item from the pool and optionally [performs a ready check](##pool-item-ready-checker)
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
- `IPoolItemFactory<TPoolItem>`
- `IPoolItemReadyCheck<TPoolItem>`
- `PoolOptions`

See [Dependency Injection](##dependency-injection) for more information.

The pool will use an [item factory](##pool-item-factory) to create new items as required.
During the lease operation, the pool invokes a [ready checker](##pool-item-ready-checker) 
to initialize an item that isn't ready.

## Pool Item Factory
Implement the `IPoolItemFactory<TPoolItem>` interface to create new items for the pool. 
The library provides a default pool implementation that uses `IServiceProvider` to construct items.
To use the default implementation, call `AddPool<TPoolItem>` or 
`AddPoolWithDefaultFactory<TPoolItem, TReadyCheckImplementation>` 
when registering the pool with the service collection.
See [Dependency Injection](##dependency-injection) for more information.

## Pool Item Ready Checker
Implement the `IPoolItemReadyCheck<TPoolItem>` interface to ensure an item is ready for use when leased from the pool.

There's a default `IPoolItemReadyCheck<TPoolItem>` implementation that always returns 
`true` from the `IsReadyAsync` method.
To use the default implementation with a custom item factory, 
call `AddPool<TPoolItem>` 
or `AddPoolWithDefaultReadyCheck<TPoolItem, TFactoryImplementation>`
when registering the pool with the service collection.
See [Dependency Injection](##dependency-injection) for more information.

Ready check is useful for items that may become inactive for some time, 
such as an SMTP connection that has been idle long enough for the server to terminate
the connection.

For example, if you're implementing an SMTP connection pool, 
the lease operation can verify the connection to the STMP server 
by invoking the SMTP `no-op`.  If the ready check fails, you can connect and authenticate to the SMTP server. 

Sample SMTP connection ready check implementation using `MailKit.IMailTransport`:
```csharp
public async ValueTask<bool> IsReadyAsync(IMailTransport item, CancellationToken cancellationToken) =>
    item.IsConnected
    && item.IsAuthenticated
    && await NoOpAsync(item, cancellationToken);
```

Sample SMTP connection `MakeReadyAsync` implementation using `MailKit.IMailTransport`:
```csharp
public async Task MakeReadyAsync(IMailTransport item, CancellationToken cancellationToken)
{
    await item.ConnectAsync(hostOptions.Host, hostOptions.Port, hostOptions.UseSsl, cancellationToken);
    await item.AuthenticateAsync(credentials.UserName, credentials.Password, cancellationToken);
}
```
## Dependency Injection
The `ServiceCollectionExtensions` class is in the `Pool.DependencyInjection` namespace.
- Call `AddPool<TPoolItem>` to register a singleton pool. Pass `Action<PoolRegistrationOptions>` to specify whether or not to register the default item factory and ready check implementations.
- Call `AddPoolItemReadyCheck<TPoolItem, TReadyCheckImplementation>` to register a singleton-ready check implementation.
- Call `AddPoolItemFactory<TPoolItem, TFactoryImplementation>` to register a singleton item factory implementation.

### Sample `AddPool<TPoolItem>` Registration
```csharp
services.AddPool<IMailTransport>(configuration, options =>
{
    // use default factory, which uses the service provider to construct pool items
    options.RegisterDefaultFactory = true;
});
```

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
</div>