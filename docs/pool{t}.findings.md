---
title: Pool<TPoolItem> Code Review — Findings & Suggestions
summary: Correctness review of src/Pool/Pool{TPoolItem}.cs — concurrency races, item-count invariants, resource leaks on failure/disposal, and lifecycle guards, with suggested fixes.
tags: [code-review, pool, note]
created: 2026-06-08
aliases: []
document.status: draft
---

# Pool<TPoolItem> Code Review — Findings & Suggestions

**Scope:** `src/Pool/Pool{TPoolItem}.cs` — the `Pool<TPoolItem>` implementation, its private `LeaseRequest`, and the `PoolItem` record struct.
**Reviewed against:** `IPool<TPoolItem>`, `PoolOptions`, `IItemFactory<T>`, `IPreparationStrategy<T>`, `IPoolMetrics`.

> ⚠️ **Concurrent refactor in progress.** Another agent is editing this file (analyzer warnings). **Line numbers below reflect the file at review time and will drift** — re-anchor by symbol name before acting.

> **Related:** the test-side review in [`test-findings.md`](./test-findings.md) — its **I1** (the `Clear_Clears_Pool` contract ambiguity) is the test-facing twin of **I2/I3** here.

---

## Status

- **RESOLVED — preparation-failure poison item.** Previously `EnsurePreparedAsync` returned a failed item to the pool via `Release(item)`, recirculating a broken item (e.g. a dropped MailKit SMTP socket) to the next leaser. Now it calls `RemoveItem(item)` (dispose + decrement) and rethrows. Fixed in the `modernized` commit; regression test `Preparation_Failure_Discards_Item_And_Serves_Fresh_One` added in `1c6d749`.
  - **Root cause:** introduced in `0f6e53d "fixed resource leak on preparation failure"` (2025-09-22). Before it, the catch had *no* cleanup — `{ metrics.RecordPreparationException(ex); throw; }` — so the item leaked (the leak that commit set out to fix). The fix added `Release(item)` where `RemoveItem(item)` was correct (`RemoveItem` already existed, used by `TryDequeue` for idle eviction), trading the leak for poison-item recirculation. So the prep-failure path never worked correctly: it leaked before `0f6e53d` and poisoned after — there was no good prior version, and it had no test coverage until `1c6d749`. Not a `Remove`→`Release` swap; a `nothing`→`Release` choice of the wrong cleanup verb.

The remaining findings are **open**.

---

## Important (correctness bugs)

### I1. Lost-wakeup race between "no item available" and enqueuing the lease request
`LeaseAsync` (lines 240–250) checks `TryAcquireItem` and, on failure, enqueues a `LeaseRequest` — but there is **no lock spanning the two**. The `gate` lock guards only `itemsAllocated`, not the `pool`/`leaseRequests` rendezvous.

- Thread A: `TryAcquireItem` returns false (pool empty, at max).
- Thread B (`Release`): `leaseRequests` is still empty → enqueues the item to `pool`.
- Thread A: enqueues its `LeaseRequest`.

End state: an item sits in `pool` and a waiter sits in `leaseRequests`, unmatched. The waiter blocks until `leaseTimeout` **even though an item is available**.

- **Why it matters:** under contention a lease can hang for the full timeout despite free capacity — the worst time for a stall.
- **Suggestion:** after enqueuing the request, re-attempt acquisition and fulfill your own request if an item is now available (double-check pattern), or coordinate `pool`/`leaseRequests` under a single lock on the slow path.

### I2. `Clear()` overwrites `itemsAllocated`, losing outstanding leases
`Clear()` (lines 307–317) calls `CreateItems(...)`, which does `itemsAllocated = count` (assignment, not `+=`; line 323). Any items currently **leased out** are dropped from the count.

Example: 10 allocated (5 idle, 5 leased) → `Clear` disposes the 5 idle, sets `itemsAllocated = initialSize`. When the 5 leased items are later released, `pool.Count` climbs past `itemsAllocated`, so `ActiveLeases = itemsAllocated - ItemsAvailable` goes **negative** and the `itemsAllocated < maxSize` cap is now wrong.

- **Why it matters:** corrupts every derived counter and the capacity gate whenever `Clear()` runs with live leases.
- **Suggestion:** `Clear()` should account for in-flight leases rather than resetting the count, and its name/contract should be reconciled (see [`test-findings.md`](./test-findings.md) I1 — the doc says "sets allocated to zero," the code re-seeds to `MinSize`).

### I3. `Clear()` does not drain the pool for non-`IDisposable` items
`Clear()` relies on `EnsureItemsDisposed()` (lines 468–480) to empty `pool`, but that method early-returns when `!IsPoolItemDisposable`:

```csharp
private void EnsureItemsDisposed()
{
    if (!IsPoolItemDisposable) return;   // <-- pool is NEVER drained here
    while (pool.TryDequeue(out var item)) (item.Item as IDisposable)?.Dispose();
}
```

So for a non-disposable `TPoolItem`, `Clear()` leaves the old items in `pool`, then `CreateItems` *adds* fresh ones on top and resets `itemsAllocated = count`. The pool grows instead of clearing; `ActiveLeases` goes negative.

- **Why it matters:** `Clear()` silently does the opposite of its name for an entire category of `TPoolItem`. Untested — `Clear_Clears_Pool` only uses `IEcho`, which *is* `IDisposable`.
- **Suggestion:** separate "drain the pool" from "dispose drained items." Drain unconditionally; dispose only the disposable ones.

### I4. Resource leak if item creation throws during construction
The constructor (line 204) eagerly fills the pool:

```csharp
pool = new(CreateItems(initialSize).Select(PoolItem.Create));
```

`CreateItems` lazily calls `itemFactory.CreateItem()` `initialSize` times. If the *k*-th call throws, items 1…k-1 were created into a `ConcurrentQueue` that is **never assigned to `pool`** — so they're never `Dispose()`d. For a disposable item (a `SmtpClient`), that leaks sockets on a failed construction.

- **Why it matters:** same family as the leak already fixed in `0f6e53d` and guarded in `TryCreateItem`'s catch — but the initial-fill path has no guard.
- **Suggestion:** materialize the initial items in a try/catch that disposes anything already created before rethrowing.

### I5. `Release()` is unguarded against use-after-dispose
`LeaseAsync` and `Clear` call `IsNotDisposed()`, but `Release` (lines 279–303) does not. After `Dispose()` has drained the pool, a `Release(item)` silently runs `pool.Enqueue(...)` into a dead pool: the item is never disposed (leak) and no `ObjectDisposedException` is thrown.

- **Why it matters:** inconsistent lifecycle guarding across the public surface; a return-after-shutdown leaks instead of failing loud.
- **Suggestion:** decide the contract — either throw `ObjectDisposedException`, or dispose the returned item when the pool is already disposed. Don't silently enqueue.

### I6. Disposal race can leak items
`Dispose()` (lines 488–503) drains `leaseRequests`, calls `EnsureItemsDisposed()`, then sets `disposed = true` **last**. A concurrent `Release` (enqueue) or `LeaseAsync`→`TryCreateItem` that interleaves *after* the drain but *before* the flag is set adds an item that is never disposed.

- **Why it matters:** disposal during active use leaks the racing item; the late flag-set widens the window.
- **Suggestion:** set `disposed = true` first (under the same synchronization used to guard acquire/release), then drain, so no new item can enter after teardown begins.

### I7. Item leak on the cancellation / hand-off race for queued leases
When `Release` hands an item to a queued waiter via `TrySetResult` (lines 67–76), it races the waiter's own cancellation. If the `cancellationToken` fires at the same instant, the result wins (the TCS is already completed, so cancel is a no-op). `LeaseAsync` (line 254) then returns the item **without re-checking cancellation**.

- **Why it matters:** a caller written to expect `OperationCanceledException` on cancel can discard the returned item it never realized it received → leak. Classic TCS-handoff hazard.
- **Suggestion:** after `await leaseRequest.Task`, if cancellation was requested, release the item back and throw `OperationCanceledException`.

### I8. Lease-wait metric stops before the queued wait actually happens
On the queued path, `RecordLeaseWaitTime(timer)` is called at line 252 — **before** `await leaseRequest.Task` at line 254. The recorded wait covers only up to the enqueue, excluding the actual time spent waiting in the queue.

- **Why it matters:** the metric under-reports wait time exactly when contention is worst — the case it exists to measure. (The fast path at line 242 is correct.)
- **Suggestion:** record the wait time after the queued task completes.

### I9. No protection against double-release / foreign release
`Release(item)` (lines 279–303) has no ownership tracking. Calling it twice on the same instance — or with an item the pool never created — enqueues duplicates, after which two leasers can receive the **same** instance.

- **Why it matters:** silent data corruption for a connection pool, and an easy caller mistake.
- **Suggestion:** track owned/leased instances (e.g. a set keyed by reference) and ignore or throw on double/foreign release.

---

## Minor / nits

- **`IsNotExpired()` is redundant (line 33).** `!Task.IsCompleted && !Task.IsCompletedSuccessfully` ≡ `!Task.IsCompleted` (success implies completion).
- **`[MethodImpl(AggressiveInlining)]` on large methods (lines 226, 278, 306).** `Release`/`Clear`/`LeaseAsync()` are loop/logging/large bodies the JIT won't inline; the hint is noise.
- **`DateTime.UtcNow` in `PoolItem` (lines 122–123).** Not `TimeProvider`-based, so idle-timeout logic isn't deterministically testable.
- **`ActiveLeases` / `ItemsAvailable` are composite non-atomic reads (lines 211–217).** `itemsAllocated` and `pool.Count` are read separately, so derived counters can be transiently inconsistent under concurrency. Metrics-only impact.
- **Idle eviction is lazy only (`TryDequeue`, lines 384–399).** An item past `idleTimeout` is disposed only when someone next tries to dequeue it; nothing reaps idle items proactively.
- **`ItemsAvailable => pool?.Count ?? 0` (line 214).** The `?.` exists only because the metric observers are registered (line 189) before `pool` is assigned (line 204); reordering would make the null-guard unnecessary.
