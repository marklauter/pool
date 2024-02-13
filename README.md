# pool
A general purpose pool for items that may require initialization, such as an SMTP or database connection.

## Lease / Release Pattern
Pooled items are placed on a queue. When a lease is requested, the pool attempts to dequeue an item. If it can, the item is returned on a task. If the item queue is empty, and the pool size is maxed out, then the lease request is queued and the lease request's TaskCompletionSource.Task is returned to the caller. The caller will block on await until timing out, or until another thread releases an item by calling Release, which scans the lease request queue for active requests before pushing the item back onto item queue.

## Pool Item Factory
The pool item factory interface makes it possible to create new items for the pool, check that items are ready for use, and make items ready for use if they are not ready. In the case of a database or SMTP connection, this would include constructing the connection, checking that the connection is active, calling connect if the connection is inactive.

## Todo
- It would be nice if args or IOptions<T> could be passed to the IPoolItemFactory.Create<T> method. Or not. Maybe the implementation can get those options injected.
- It might be nice if IPoolItemFactory had a CreateAsync method

## Dev Log
- 12 FEB 2024 - started SMTP pool at the end of 2023, but got busy with other stuff. Will take it up again soon though because I need it for a work project.
