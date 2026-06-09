---
title: Pool<TPoolItem> Code Review — Findings & Suggestions
summary: Correctness review of src/Pool/Pool{TPoolItem}.cs — concurrency races, item-count invariants, resource leaks on failure/disposal, and lifecycle guards. Tracks resolved items, the defined Clear contract, and what remains open.
tags: [code-review, pool, note]
created: 2026-06-08
aliases: []
document.status: resolved
---

# Pool<TPoolItem> Code Review — Findings & Suggestions

**Scope:** `src/Pool/Pool{TPoolItem}.cs` — the `Pool<TPoolItem>` implementation (now a `SemaphoreSlim` capacity gate plus one idle `ConcurrentQueue`) and the `PoolItem` record struct.
**Reviewed against:** `IPool<TPoolItem>`, `PoolOptions`, `IItemFactory<T>`, `IPreparationStrategy<T>`, `IPoolMetrics`.

> The SemaphoreSlim redesign has landed (I1/I7 resolved; see Resolved). **Line numbers are approximate — anchor by symbol name.**

> **Related:** the test-side review in [`pool-tests.findings.md`](./pool-tests.findings.md).

---

## Resolved

- **Preparation-failure poison item.** `EnsurePreparedAsync` previously returned a failed item to the pool via `Release(item)`, recirculating a broken item (e.g. a dropped MailKit SMTP socket). Now it calls `DisposeItem(item)` and rethrows; the held permit is released by `LeaseAsync`'s catch (`_ = gate.Release()`), so there is no count to decrement (the counters are derived). Regression test `Preparation_Failure_Discards_Item_And_Serves_Fresh_One`.
  - **Root cause:** introduced in `0f6e53d "fixed resource leak on preparation failure"` (2025-09-22). Before it the catch had *no* cleanup, so the item leaked (the leak that commit set out to fix); the fix added `Release(item)` where a dispose-and-discard (today's `DisposeItem(item)`) was correct, trading the leak for poison-recirculation. The prep path never worked correctly — it leaked before `0f6e53d` and poisoned after. Not a discard→`Release` swap; a `nothing`→`Release` choice of the wrong cleanup verb.
- **I3 — `Clear()` now drains non-disposable pools.** `EnsureItemsDisposed` → renamed `DrainPoolAndDisposeItems`, which now unconditionally dequeues the whole queue and routes each item through `DisposeItem` (which itself no-ops the actual dispose for non-disposable `T`) — there is no longer a `!IsPoolItemDisposable` early-return. `Clear`/`Dispose` empty the pool for non-disposable `T` too.
- **I5 — `Release()` use-after-dispose.** `Release` now opens with `ThrowIfDisposed();`, so a release after `Dispose()` throws `ObjectDisposedException` instead of silently leaking into a drained pool — consistent with `LeaseAsync`/`Clear`.
- **I6 — disposal ordering.** `Dispose()` now sets `disposed = true` **first**, before draining waiters and the pool, narrowing the teardown race.
- **I8 — lease-wait metric timing.** The queued path now `await`s the hand-off, *then* calls `RecordLeaseWaitTime`, so the metric captures the real queue wait rather than only the time up to enqueue.
- **I1 (lost-wakeup) and I7 (cancel/hand-off race) — eliminated by the SemaphoreSlim redesign.** `LeaseAsync`/`Release` no longer rendezvous two queues through a hand-rolled `LeaseRequest` (TCS + linked CTSs). Capacity is now a single `SemaphoreSlim(maxSize, maxSize)` — a permit is the right to one item — plus the one idle `ConcurrentQueue`. With one structure there is no second queue to fall out of sync with (I1 gone), and no TCS to race a result against a cancel (I7 gone). The lease timeout still surfaces as `TaskCanceledException` (zero observable change — `WaitAsync`-returns-`false` is translated back to it). Regression test `Lease_Does_Not_Lose_Wakeups_Under_Concurrency` is red on the old rendezvous (a waiter times out under a Barrier-aligned release/queue collision) and green on the semaphore. The ~120-line `LeaseRequest` class and the two `CA2000` suppressions that vouched for its disposal are deleted.
  - **Derived counters (bonus).** `itemsAllocated` and its `Lock` are gone: `ActiveLeases = maxSize - gate.CurrentCount`, `ItemsAvailable = pool.Count`, `ItemsAllocated = ActiveLeases + ItemsAvailable`. The counters cannot desync from the items because they are computed from live state — which retires the old Clear count-clobber concern entirely. `QueuedLeases` is the one explicit counter the semaphore hides (`Interlocked`, incremented only on the contended slow path).
- **I4 — resource leak on a mid-seed factory failure during construction.** The constructor seeded with `pool = new(CreateItems(initialSize).Select(PoolItem.Create))`, assigning `pool` only once the whole expression completed — so if the *k*-th `itemFactory.CreateItem()` threw, the items already created were abandoned undisposed (leaked sockets for a disposable `TPoolItem`). Now the ctor assigns `pool` to an empty queue **first**, seeds it through the restored lazy `CreateItems` (`yield`) iterator, and on any throw calls `DrainPoolAndDisposeItems()` before rethrowing — the same drain-and-dispose path `Clear`/`Dispose` use. `gate` is constructed only after seeding succeeds, so a failed construction also abandons no `SemaphoreSlim`. `Clear`'s reseed loop was already leak-safe (it enqueues each item immediately, so partials live in `pool` and are reclaimed by the next `Clear`/`Dispose`). Regression test `Ctor_Disposes_Already_Created_Items_When_Factory_Throws_Mid_Seed` (red before, green after).

---

## Clear — defined contract (strict-max)

`Clear()` is specified as:

1. Drain and dispose all currently-idle (pooled) items.
2. Refill idle with **fresh** items to `min(MinSize, free capacity)`, where free capacity is `gate.CurrentCount`. Idle items hold no permit, so this never touches the semaphore and cannot over-allocate past `maxSize`.
3. Queued waiters are **not** served by `Clear` — they are served by normal returns (`Release`), which the semaphore already guarantees.

Tests: `Clear_Disposes_Idle_Items_And_Refills_With_Fresh_Ones` and `Clear_Does_Not_Over_Allocate_For_Queued_Requests` (the latter replaces `Clear_Fulfills_Queued_Lease_Requests`). The `IPool<T>.Clear` XML doc was corrected to describe dispose-idle + refill-to-`MinSize`, with waiters served by returns.

- **Former limitation (was I2) — gone.** The previous contract over-created past `maxSize` to hand items to queued waiters, transiently exceeding the cap and leaving the count under-reporting until leases returned. Strict-max removes that by construction (refill only up to free capacity), and the derived counters can no longer under-report. This was chosen as both the cleaner design and a fix, not a concession.

---

## Open — Important

_None — all important findings are resolved._

---

## Won't fix (by design)

### I9. No protection against double-release / foreign release
`Release(item)` has no ownership tracking. Called twice on the same instance — or with an item the pool never created — it enqueues duplicates, after which two leasers can receive the **same** instance.

**Disposition: won't fix — caller-contract violation (user error).** Releasing an item you do not hold (or releasing it twice) is misuse, in the same vein as `ArrayPool<T>.Return`-ing a buffer twice. The pool documents lease/release as a balanced pair and does not police it. A reference-keyed owned/leased registry was considered and declined: it adds per-lease bookkeeping (and a lock) to guard against a misuse the caller fully controls.

- **For the record — the misuse's blast radius:** `Release` enqueues the item *before* `gate.Release()`, so a double/foreign release enqueues the duplicate, then `gate.Release()` either throws `SemaphoreFullException` (only when already at capacity) or **silently inflates the permit count** — an over-counted `gate.CurrentCount` lets the pool subsequently create *past* `maxSize`. This is a *new* failure mode versus the pre-redesign aliasing (which never breached the cap), so the semaphore did **not** tighten double-release safety. Noted so the decision is informed — it does not change the disposition.

---

## Minor / nits  *(line numbers approximate)*

- **Derived counters are composite non-atomic reads.** `ActiveLeases` reads `gate.CurrentCount`, and `ItemsAllocated` adds `pool.Count`, read separately — so the counters can be transiently inconsistent under concurrency (e.g. `Release` enqueues before releasing the permit, so `ItemsAllocated` briefly over-counts by one). Metrics-only impact.
- **Idle eviction is lazy only** (`TryDequeue` in `TryAcquireItem`). An item past `idleTimeout` is disposed only when someone next tries to dequeue it; nothing reaps idle items proactively.

**Resolved nits:** `IsNotExpired()` (gone with `LeaseRequest`); `[MethodImpl(AggressiveInlining)]` dropped from `Release`; `ItemsAvailable => pool?.Count ?? 0` simplified to `pool.Count` after the ctor reorder. **`DateTime.UtcNow` in `PoolItem` (was M4, idle portion):** the idle clock now reads an injected `TimeProvider` (optional trailing ctor param, defaults to `TimeProvider.System`; `PoolItem` stamps and is judged against `timeProvider.GetUtcNow()`), so idle-timeout eviction is deterministically testable — regression test `Idle_Item_Past_Timeout_Is_Disposed_And_Fresh_One_Served` advances a `FakeTimeProvider`. The lease-timeout (`gate.WaitAsync(leaseTimeout)` — `SemaphoreSlim` has no `TimeProvider` overload) and preparation-timeout (`new CancellationTokenSource(preparationTimeout)`) still read wall-clock; see `pool-tests.findings.md` M4.
