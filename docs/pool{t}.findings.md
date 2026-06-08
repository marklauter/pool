---
title: Pool<TPoolItem> Code Review — Findings & Suggestions
summary: Correctness review of src/Pool/Pool{TPoolItem}.cs — concurrency races, item-count invariants, resource leaks on failure/disposal, and lifecycle guards. Tracks resolved items, the defined Clear contract, and what remains open.
tags: [code-review, pool, note]
created: 2026-06-08
aliases: []
document.status: draft
---

# Pool<TPoolItem> Code Review — Findings & Suggestions

**Scope:** `src/Pool/Pool{TPoolItem}.cs` — the `Pool<TPoolItem>` implementation, its private `LeaseRequest`, and the `PoolItem` record struct.
**Reviewed against:** `IPool<TPoolItem>`, `PoolOptions`, `IItemFactory<T>`, `IPreparationStrategy<T>`, `IPoolMetrics`.

> ⚠️ **File is under active edit** (warnings cleanup + ongoing redesign). **Line numbers are approximate — anchor by symbol name.**

> **Related:** the test-side review in [`test-findings.md`](./test-findings.md).

---

## Resolved

- **Preparation-failure poison item.** `EnsurePreparedAsync` previously returned a failed item to the pool via `Release(item)`, recirculating a broken item (e.g. a dropped MailKit SMTP socket). Now it calls `RemoveItem(item)` (dispose + decrement) and rethrows. Regression test `Preparation_Failure_Discards_Item_And_Serves_Fresh_One`.
  - **Root cause:** introduced in `0f6e53d "fixed resource leak on preparation failure"` (2025-09-22). Before it the catch had *no* cleanup, so the item leaked (the leak that commit set out to fix); the fix added `Release(item)` where `RemoveItem(item)` was correct, trading the leak for poison-recirculation. The prep path never worked correctly — it leaked before `0f6e53d` and poisoned after. Not a `Remove`→`Release` swap; a `nothing`→`Release` choice of the wrong cleanup verb.
- **I3 — `Clear()` now drains non-disposable pools.** `EnsureItemsDisposed` → renamed `DrainPoolAndDisposeItems`, and the `!IsPoolItemDisposable` branch now drains the queue unconditionally (disposing only the disposable items) instead of early-returning. `Clear`/`Dispose` empty the pool for non-disposable `T` too.
- **I5 — `Release()` use-after-dispose.** `Release` now opens with `_ = IsNotDisposed();`, so a release after `Dispose()` throws `ObjectDisposedException` instead of silently leaking into a drained pool — consistent with `LeaseAsync`/`Clear`.
- **I6 — disposal ordering.** `Dispose()` now sets `disposed = true` **first**, before draining waiters and the pool, narrowing the teardown race.
- **I8 — lease-wait metric timing.** The queued path now `await`s the hand-off, *then* calls `RecordLeaseWaitTime`, so the metric captures the real queue wait rather than only the time up to enqueue.

---

## Clear — defined contract

`Clear()` is now specified as:

1. Drain and dispose all currently-idle (pooled) items.
2. Refill the pool with **fresh** items to `max(QueuedLeases, initialSize)`.
3. Hand fresh items to any queued waiters; the remainder stock the pool.

Tests encoding this (replacing the misleadingly-named `Clear_Clears_Pool`): `Clear_Disposes_Idle_Items_And_Refills_With_Fresh_Ones` and `Clear_Fulfills_Queued_Lease_Requests`. This resolves the contract ambiguity in [`test-findings.md`](./test-findings.md) I1.

- **Doc follow-up (open):** the `IPool<T>.Clear` XML doc still reads *"clears the pool and sets allocated to zero,"* which contradicts the defined re-seed behavior — it should be corrected to describe dispose-idle + refill-to-`MinSize` + fulfill-waiters.
- **Accepted limitation (was I2):** calling `Clear()` while the pool is **saturated with outstanding leases** temporarily over-allocates past `maxSize` and leaves `itemsAllocated` under-counting until those leases return (returns currently re-pool instead of shedding). This is accepted as ADO `ClearPool`-style behavior — you call `Clear`, it clears, and the transient excess drains as items come back. To make the count reconverge *exactly* (no tracking needed), the agreed-but-unimplemented option is: count `itemsAllocated` incrementally (drop the `itemsAllocated = count` reset; drain decrements, create `+=`) and **shed on return** — `Release` disposes the returned item instead of pooling it while `itemsAllocated > maxSize`.

---

## Open — Important

### I1. Lost-wakeup race between "no item available" and enqueuing the lease request  *(the one remaining critical, contract-independent bug)*
`LeaseAsync` checks `TryAcquireItem` and, on failure, enqueues a `LeaseRequest` — with **no lock spanning the two**. `gate` guards only `itemsAllocated`, not the `pool`/`leaseRequests` rendezvous.

- A: `TryAcquireItem` → false (pool empty, at max).
- B (`Release`): `leaseRequests` still empty → `pool.Enqueue(item)`.
- A: enqueues its `LeaseRequest`.

End state: an item sits in `pool`, a waiter sits in `leaseRequests`, unmatched. The waiter is only woken by a *future* `Release`. Because `leaseTimeout` defaults to `Timeout.InfiniteTimeSpan`, if the system goes quiescent the waiter **hangs forever** despite an idle item being available.

- **Suggestion:** after enqueuing the request, re-attempt acquisition and fulfill your own request if an item is now available (double-check), or coordinate `pool`/`leaseRequests` under a single lock on the slow path.

### I4. Resource leak if item creation throws during construction
The constructor eagerly fills the pool: `pool = new(CreateItems(initialSize).Select(PoolItem.Create))`. `CreateItems` now builds an eager `List` by calling `itemFactory.CreateItem()` `initialSize` times. If the *k*-th call throws, items 1…k-1 are created but the list/queue is never assigned to `pool` — so they're never `Dispose()`d. For a disposable item (a `SmtpClient`) that leaks sockets on a failed construction.

- **Suggestion:** materialize the initial items in a try/catch that disposes anything already created before rethrowing — the same guard `TryCreateItem` already has on its create path.

### I7. Item leak on the cancellation / hand-off race for queued leases
When `Release` hands an item to a queued waiter via `TrySetResult`, it races the waiter's own cancellation. If the token fires at the same instant, the result wins (the TCS is already completed) and `LeaseAsync` returns the item **without re-checking cancellation**.

- **Why it matters:** a caller expecting `OperationCanceledException` on cancel can drop the item it never realized it received → leak.
- **Suggestion:** after `await leaseRequest.Task`, if cancellation was requested, release the item back and throw `OperationCanceledException`.

### I9. No protection against double-release / foreign release
`Release(item)` has no ownership tracking. Called twice on the same instance — or with an item the pool never created — it enqueues duplicates, after which two leasers can receive the **same** instance.

- **Suggestion:** track owned/leased instances (e.g. a set keyed by reference) and ignore or throw on double/foreign release. (A leased-item registry would also enable the generation-free staleness story if ever needed.)

---

## Minor / nits  *(line numbers approximate)*

- **`IsNotExpired()` is redundant.** `!Task.IsCompleted && !Task.IsCompletedSuccessfully` ≡ `!Task.IsCompleted` (success implies completion).
- **`[MethodImpl(AggressiveInlining)]` on large methods** (`Release`/`Clear`/`LeaseAsync()`). Loop/logging/large bodies the JIT won't inline; the hint is noise.
- **`DateTime.UtcNow` in `PoolItem`.** Not `TimeProvider`-based, so idle-timeout logic isn't deterministically testable (also flagged as `test-findings.md` M4).
- **`ActiveLeases` / `ItemsAvailable` are composite non-atomic reads.** `itemsAllocated` and `pool.Count` are read separately, so derived counters can be transiently inconsistent under concurrency. Metrics-only impact.
- **Idle eviction is lazy only** (`TryDequeue`). An item past `idleTimeout` is disposed only when someone next tries to dequeue it; nothing reaps idle items proactively.
- **`ItemsAvailable => pool?.Count ?? 0`.** The `?.` exists only because the metric observers are registered before `pool` is assigned in the ctor; reordering would make the null-guard unnecessary.
