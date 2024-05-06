![pool logo](https://raw.githubusercontent.com/marklauter/pool/main/images/pool.png)
# Pool
A general purpose pool for items that may require initialization such as SMTP or database connections.

## Lease / Release Pattern
Pooled items are placed on a queue. When a lease is requested, the pool attempts to dequeue an item. If it can, the item is returned on a task. If the item queue is empty, and the pool size is maxed out, then the lease request is queued and the lease request's TaskCompletionSource.Task is returned to the caller. The caller will block on await until timing out, or until another thread releases an item by calling Release, which scans the lease request queue for active requests before pushing the item back onto item queue.

## Pool Item Factory
The pool item factory interface makes it possible to create new items for the pool and make items ready for use if they are not ready. There's a default pool implementation that uses `IServiceProvider` to construct items, but you can write your own factory implementation.

## Ready Checker
The ready checker makes it possible to ensure items are ready for use before they are leased. This is useful for items that may become inactive after a period of time, such as a database connection that has been idle for a while.

## Pool
The pool uses the item factory and the ready checker to create and manage item readiness. For example, if you pool a database or SMTP connection, the lease operation would include pulling a connection off the queue or constructing it if the queue is empty, then checking that the connection is active, and calling connect if the connection is inactive.

## Dependency Injection
The `ServiceCollectionExtensions` class is in the `Pool.DependencyInjection` namespace.
- Call `AddPool<TPoolItem, TFactoryImplementation>` to register a singleton pool with your own item factory implementation.
- Call `AddPoolWithDefaultFactory<TPoolItem>` to register a singleton pool with the default item factory. The default factory uses the service provider to construct pool items.
- Call `AddReadyCheck<TPoolItem, TReadyCheck>` to register your own `IReadyCheck` implementation. If a ready check is not registered, the pool will not check item readiness before leasing.

## Dev Log
- 12 FEB 2024 - started SMTP pool at the end of 2023, but got busy with other stuff. Will take it up again soon though because I need it for a work project.
- 05 MAY 2024 - prepping for publish to Nuget.
