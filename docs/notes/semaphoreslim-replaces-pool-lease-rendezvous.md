---
title: SemaphoreSlim replaces the pool's hand-rolled lease/release rendezvous
summary: Modeling pool capacity as a SemaphoreSlim plus one idle-item queue deletes the LeaseRequest/TCS machinery in Pool<TPoolItem> and removes the lost-wakeup and cancel/hand-off races by construction. The open decision is whether Clear keeps over-creating items to serve queued waiters.
tags: [pool, concurrency, design, note, decision]
created: 2026-06-08
aliases: []
document.status: resolved
---

# SemaphoreSlim replaces the pool's hand-rolled lease/release rendezvous

Modeling capacity as a `SemaphoreSlim` plus one idle-item queue deletes the `LeaseRequest`/TCS machinery in `Pool<TPoolItem>` and removes the lost-wakeup and cancel/hand-off races by construction.

## current design (observation)

`LeaseAsync` rendezvouses two independent lock-free queues — `ConcurrentQueue<PoolItem>` (idle items) and `ConcurrentQueue<LeaseRequest>` (waiters) — with no lock spanning the check-then-enqueue. Each waiter is a `LeaseRequest` wrapping a `TaskCompletionSource<TPoolItem>` plus up to three `CancellationTokenSource`/registration fields to enforce `leaseTimeout` and the caller's token. `Release` walks the waiter queue calling `TrySetResult`.

This is the root of the concurrency findings in [[docs/pool{t}.findings.md]]: I1 (lost-wakeup — an item lands in the idle queue while a waiter sits unmatched and, under the default infinite `leaseTimeout`, hangs forever) and I7 (cancel vs `TrySetResult` race).

## the replacement (proposal)

```
SemaphoreSlim gate = new(maxSize, maxSize);   // permits == free capacity
ConcurrentQueue<PoolItem> idle;

LeaseAsync(ct):
    if (!await gate.WaitAsync(leaseTimeout, ct)) throw <timeout>;
    try   { var item = idle.TryDequeue(out var p) && NotExpired(p) ? p.Item : Create();
            return await EnsurePreparedAsync(item, ct); }
    catch { gate.Release(); throw; }          // never leak the permit

Release(item): idle.Enqueue(new(item)); gate.Release();
```

Holding a permit is the right to one item, so `created <= maxSize` falls out for free; `WaitAsync(leaseTimeout, ct)` subsumes the wait, the pool timeout, and caller cancellation in one call (no per-waiter token written by hand). `QueuedLeases` needs an explicit `Interlocked` counter — the semaphore hides its waiter count.

## what it deletes

The entire `LeaseRequest` class (~120 lines including the waiter queue, the `Release` walk-loop, the `Dispose` drain, and the linked-CTS plumbing), plus the CA2000 suppressions that exist only to vouch for its disposal. The lease-wait timeout token moves from hand-written code into `WaitAsync` — the framework still allocates a timer-CTS for a finite timeout, so the win is maintenance and bug surface, not allocation.

## what it fixes by construction

- I1 cannot occur — one queue, no second structure to fall out of sync with.
- I7 cannot occur — no TCS to race a result against a cancel.
- `false`-on-timeout vs throw-on-cancel lets the pool surface a distinct `TimeoutException` (today both arrive as `TaskCanceledException`; see `test-findings.md` I3).

## the open decision (the only real fork)

A strict `SemaphoreSlim(maxSize, maxSize)` throws `SemaphoreFullException` when `Release` would exceed max, so it cannot manufacture items beyond the cap to hand to queued waiters — which is exactly what `Clear` does today (and what `Clear_Fulfills_Queued_Lease_Requests` asserts). Pick one:

- (a) strict max — `Clear` stops over-creating; queued waiters are served by normal returns instead. Cleaner, respects `maxSize`, changes that test.
- (b) keep over-create — use an unbounded `SemaphoreSlim(maxSize)` plus manual max checks, reintroducing the count management the swap was meant to delete.

Secondary decision: keep `TaskCanceledException` on timeout (zero observable change) versus take the `TimeoutException` improvement (a real break for a published v6 package).

## effort and logistics (inference)

About half a day for the rewrite plus revalidation. Everything except `Clear` maps 1:1 — counters, idle eviction, MinSize seed, preparation plus the poison-item fix, dispose (dispose the semaphore; parked `WaitAsync` then throws `ObjectDisposedException`). The one care item is permit-leak discipline: every successful `WaitAsync` pairs with a `Release` on every exit path. `Pool{TPoolItem}.cs` is under concurrent edit by a warnings agent, so draft in a side file and swap in once it clears. Add a concurrency stress test for the rendezvous — one that fails against today's lost-wakeup and passes against the semaphore.

## addendum — review pushback (what to get right)

A review pass agrees with the direction: the semaphore makes I1 and I7 *unrepresentable* rather than patched, and `created <= maxSize` is a free consequence of "a permit is the right to one item." Four conditions make it a *safe* yes, plus one clarification on the `Clear` fork.

### 1. make permit-leak discipline structural, not manual

"Every `WaitAsync` pairs with a `Release` on every exit path" is the entire risk surface, and the leak-prone paths are real: a throw from item creation, a throw or cancellation from `EnsurePreparedAsync`, and cancellation observed *after* the permit is taken. Don't hand-audit it across paths. Wrap the permit in an RAII holder — a `readonly struct` that releases on `Dispose` and is flipped to "owned by the caller" only on the successful hand-off — so "release exactly once" is enforced by the type, not by reviewer vigilance. This is the one place a regression can hide.

### 2. keep `TaskCanceledException` on timeout for v6

The distinct-`TimeoutException` improvement is real, but it is an observable break for a published package. The rewrite should be behaviorally invisible except that it stops hanging: translate `WaitAsync`-returns-`false` back into the same `TaskCanceledException` callers see today, and save the nicer exception for a deliberate major bump. Do not bundle a behavior break into a bug fix.

### 3. `Clear` → option (a), and treat it as a fix, not a concession

A clarification the fork framing obscures: **idle items do not hold permits** — the permit was released when the item was returned. So `Clear` draining and reseeding the idle queue never touches the semaphore; the *only* thing strict semantics forbid is manufacturing items beyond `maxSize` to wake parked waiters. But that over-create is a latent bug today — it transiently exceeds `maxSize`. Redefine `Clear` as "dispose idle + reseed MinSize"; parked waiters are served by normal returns, which the semaphore already guarantees. Option (a) is the better design, not merely the cheaper one — it happens to change `Clear_Fulfills_Queued_Lease_Requests`.

### 4. land it behind a failing-first concurrency test

Trade a known-racy design for a proven one: a bounded-pool, at-capacity, concurrent lease/release test that is **red on today's code** — use a finite `leaseTimeout` so the lost-wakeup *fails* rather than hangs CI — and **green on the semaphore**. That test is the evidence the swap is correct, not just plausible.

### the conditions, together

Worth doing, given: RAII permit handling (1), an unchanged exception surface for now (2), `Clear` option (a) (3), and a failing-first stress test (4). The payoff is bug-surface and ~120 deleted lines — including the two `CA2000` suppressions, which exist only to vouch for the `LeaseRequest` disposal — not allocation.

## outcome (landed)

Implemented as a production-only change. Locked decisions: strict-max `SemaphoreSlim(maxSize, maxSize)` (option a), and lease timeout keeps surfacing as `TaskCanceledException` (zero observable change; `WaitAsync`-returns-`false` is translated back to it).

- **Permit-leak guard (condition 1):** chosen as an acquire-then-`try`/`catch` that releases the permit on every failure path, with the invariant marked in a comment, rather than a struct holder — same exactly-once guarantee, no per-lease allocation. A single outer `catch` records every lease exception (closing the disposed-fast-path gap).
- **Derived counters (bonus beyond the original ask):** `itemsAllocated` and its `Lock` are deleted — `ActiveLeases = maxSize - gate.CurrentCount`, `ItemsAvailable = pool.Count`, `ItemsAllocated` their sum. Counts can no longer desync from the items. `QueuedLeases` is the one explicit `Interlocked` counter (slow path only).
- **`Clear` (condition 3):** disposes idle + reseeds to `min(MinSize, gate.CurrentCount)`; waiters served by returns. The old over-create-past-`maxSize` limitation (was findings I2) is gone by construction.
- **I9 not fixed (review correction):** double/foreign `Release` is "no worse, occasionally louder" — the duplicate is enqueued before `gate.Release()`, which only throws `SemaphoreFullException` when already saturated. Left Open; needs per-item lease-identity tracking.
- **Tests (condition 4):** the failing-first `Lease_Does_Not_Lose_Wakeups_Under_Concurrency` is red on the old rendezvous and green on the semaphore. Forced edits to two existing tests (`Lease_Queues_Request` reorder for the async hand-off; `Clear_Fulfills_Queued_Lease_Requests` → `Clear_Does_Not_Over_Allocate_For_Queued_Requests`). One lifecycle test added to restore branch coverage over the ratchet floor (disposed-guard + double-dispose, documented gaps in `test-findings.md`).
- **Verification:** build-gate green (format, build, 28 tests, coverage 90.14/63.15/91.54 line/branch/method, floors 85/60/90).

See [[docs/pool{t}.findings.md]] for the per-finding status.
