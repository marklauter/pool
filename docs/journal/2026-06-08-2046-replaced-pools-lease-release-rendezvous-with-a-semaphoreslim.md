---
title: Replaced Pool's lease/release rendezvous with a SemaphoreSlim
summary: The hand-rolled LeaseRequest/TaskCompletionSource rendezvous across two ConcurrentQueues collapsed to one SemaphoreSlim capacity gate plus an idle queue, making the I1 lost-wakeup and I7 cancel/hand-off races unrepresentable; it landed behind a failing-first concurrency test, deleting ~120 lines and the two CA2000 suppressions that only vouched for the old machinery.
tags: [pool, concurrency, refactor, journal, semaphoreslim]
created: 2026-06-08
aliases: []
---

# Replaced Pool's lease/release rendezvous with a SemaphoreSlim

`Pool<TPoolItem>` replaced its hand-rolled `LeaseRequest`/`TaskCompletionSource` rendezvous over two `ConcurrentQueue`s with a single `SemaphoreSlim` capacity gate plus one idle queue. The I1 lost-wakeup and I7 cancel/hand-off races are now unrepresentable rather than patched.

## context

The old `LeaseAsync` rendezvoused two independent lock-free queues — idle items and waiters — with no lock spanning the check-then-enqueue. A `Release` could drop an item into the idle queue while a waiter sat unmatched; under the default infinite `LeaseTimeout` that waiter hung forever (finding I1). Each waiter carried a `TaskCompletionSource` plus up to three `CancellationTokenSource`s to enforce the timeout and the caller's token, and a cancel could race the release's `TrySetResult` (I7). The design rationale and the open decisions live in [[semaphoreslim-replaces-pool-lease-rendezvous]].

## the plan and the review

The plan modeled capacity as a single `SemaphoreSlim(maxSize, maxSize)` — a permit is the right to one item — plus one idle `ConcurrentQueue`. A review pass (the note's addendum) set four conditions: structural permit-leak handling, keep `TaskCanceledException` for the published v6, strict-max `Clear`, and a failing-first concurrency test. Two pushbacks on the first plan draft held up. The "I9 double-release now fails loud" claim was overstated — `Release` enqueues before `gate.Release()` can throw, and only throws when saturated — so I9 stays unresolved (observation). And the stress test belonged in scope, not deferred as an opt-in follow-up. Both were taken.

## what changed

- `LeaseAsync` takes a permit (fast `Wait(0)`, else `await WaitAsync(leaseTimeout, ct)`), then a `try`/`catch` releases it on any failure path and transfers ownership to the caller on success. A `WaitAsync` that returns `false` is translated back to `TaskCanceledException`, so the v6 contract holds.
- `Release` enqueues the item *before* releasing the permit, so a woken waiter always finds it.
- The `LeaseRequest` class, its waiter queue, the `Release` walk-loop, the linked-CTS plumbing, and the `itemsAllocated` `Lock` all disappeared (~120 lines), along with the two `CA2000` suppressions that existed only to vouch for `LeaseRequest` disposal.
- Counters are derived: `ActiveLeases = maxSize - gate.CurrentCount`, `ItemsAllocated = ActiveLeases + ItemsAvailable`. The manual counter — and its `Clear`-clobber drift — is gone.
- `Clear` is strict-max: dispose idle, reseed `MinSize` capped by free capacity, never manufacture past `maxSize`. Queued waiters are served by real returns.

## what it fixes by construction

I1 cannot occur — one structure, no second queue to fall out of sync with (observation). I7 cannot occur — no TCS to race a cancel (observation). The default infinite-timeout hang turns into correct blocking (inference).

## verification

A failing-first regression test, `Lease_Does_Not_Lose_Wakeups_Under_Concurrency`, provokes the race in isolation: a single-slot pool, a finite `LeaseTimeout`, and a spin-barrier that collides `Release` with a queuing lease inside the check-then-enqueue window. It goes red on the old code (the lost wakeup times out) and green on the semaphore. The full suite is green under `TreatWarningsAsErrors`, and the deleted `CA2000` suppressions left no orphaned warning.

## still open

I4 (a ctor leak if a factory call throws mid-seed) and I9 (double-release — the swap does not fix it; a stray release can inflate permits and breach `maxSize`). Both deferred. See [[semaphoreslim-replaces-pool-lease-rendezvous]] and `docs/pool{t}.findings.md`.
