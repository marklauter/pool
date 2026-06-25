---
title: Hold-timeout reclaims a capacity slot, not the leased item
summary: A bounded-pool hold-timeout breaks lease-starvation deadlock by reclaiming one unit of capacity (allocated = idle + leased, capped at maxSize) after a caller holds an item too long. The item itself stays in the caller's hands and is never repatriated; a slow return whose slot was already reclaimed is dropped, not re-pooled. Requires per-lease tracking and exactly-once slot accounting.
tags: [pool, concurrency, design, note, decision]
created: 2026-06-15
document.status: proposed
---

# Hold-timeout reclaims a capacity slot, not the leased item

A bounded-pool hold-timeout reclaims one unit of capacity after a caller holds an item past a deadline, so the pool heals from callers that never return. It reclaims the slot, never the item.

## the deadlock it solves (observation)

The bounded pool caps `allocated = idle + leased` at `maxSize` (a permit per leased item; see [[docs/notes/semaphoreslim-replaces-pool-lease-rendezvous.md]]). When every slot is leased and callers never return — a crash, a bug, an unbounded hold — `LeaseAsync` can neither hand out an idle item nor create a new one. The pool is permanently starved.

## the model (proposal)

The tracked quantity is `allocated = idle + leased`, capped at `maxSize`. The hold-timeout reclaims one unit of that capacity — a slot — not the item.

- You cannot repatriate the item; it is in the caller's hands. The pool makes no attempt to reclaim, reuse, or dispose it from the timer.
- Reclaiming the slot decrements `leased`, which frees capacity, which lets the next `LeaseAsync` create a fresh item instead of waiting forever.
- The timed-out item leaves the books entirely — neither idle nor counted-leased. It is abandoned in the caller's hands.

In today's code the semaphore's permit count is the capacity counter (`ActiveLeases = maxSize - gate.CurrentCount`), so "decrement leased" and "release one permit" are the same operation. The contract is a slot reclaim; the permit is the implementation.

## naming (decision)

This is not the existing `LeaseTimeout`, which is the acquire timeout — how long a caller waits in the queue to get an item. The new field is a hold timeout — how long a caller may keep an item before its slot is reclaimed. Distinct names: `LeaseTimeout` for acquire, `MaxLeaseDuration` (or `AbandonedLeaseTimeout`) for hold. Conflating them is the main readability trap.

## exactly-once: a slot is freed once (inference)

The correctness invariant: a slot is freed exactly once, by either the hold-timer or the caller's return, never both. Two releasers now exist for one lease, so each handed-out lease needs an `Interlocked` flag deciding the winner.

- Return wins the flag — normal path: enqueue idle, free the slot, cancel the timer.
- Timer wins the flag — free the slot, mark the lease abandoned. The timer cannot touch the item.
- Slow return after the timer won — the slot already belongs to someone else, so the return must not free capacity again and must not re-pool the item. It disposes the item and walks away. This is the bounded analog of the unbounded pool's drop-when-full.

Freeing twice would read `allocated` one too low and let the pool create an extra live item past `maxSize`.

## what it forces into the bounded pool (inference)

The pool today keeps zero state about leased items — the gate counts them, the items live in callers' hands. The hold-timeout requires:

1. Per-lease tracking: a reference-keyed `ConcurrentDictionary<TPoolItem, LeaseSlot>` (an item is leased to one caller, so identity is a safe key) plus an `ITimer` per active lease via `TimeProvider.CreateTimer` for deterministic tests. This is the same per-item lease-identity tracking that finding I9 (double/foreign release) needs — see [[docs/notes/pool{t}.findings.md]].
2. `Dispose` stops every outstanding timer, or a timer fires post-disposal and frees a slot on a disposed semaphore — an unobserved background exception.
3. `ItemsAllocated` softens from exact to approximate: a reclaimed-but-not-yet-returned item is alive but off the books, so the count undercounts true live items. Document it; add a reclaimed/abandoned counter so the gap is observable.
4. Zero cost when off: all of it lights up only when the hold timeout is finite. Default infinite means no dict, no timers, today's exact behavior.

## sequencing (decision)

This feature adds to the bounded pool, which cuts against the goal of simplifying it. Build it last: ship the unbounded pool first, extract the shared item-store, then add slot-reclaim on the cleaned-up base where the dict-of-active-leases has a natural home.
