---
title: Pool.Tests review — open findings
summary: The still-open findings from the Pool.Tests review after the SemaphoreSlim refactor and test rewrite — a dead config key, a brittle BCL-message assertion, the unasserted distinctness invariant, indirect and duplicate tests, wall-clock timing, and coverage gaps. Resolved items dropped; production-side status in pool{t}.findings.md.
tags: [test-review, pool, note, todo]
created: 2026-06-08
aliases: []
document.status: open
---

# Pool.Tests review — open findings

The findings from the `src/Pool.Tests` review that remain valid against the current code, after the SemaphoreSlim redesign landed and the test file was rewritten.

Scope: `tests/Pool.Tests` — `PoolTests.cs`, `NamedPoolTests.cs`, `PoolItemFactoryTests.cs`, `PoolConstructorTests.cs`, `Startup.cs`. Reviewed against `src/Pool`. Finding IDs (I2, I3, M4, …) are preserved from the original triage so cross-references resolve; the gaps in the sequence are the resolved items, dropped here. Line numbers drift — anchor by symbol name.

Resolved and dropped (verified closed against current code): I1 (Clear contract — doc corrected, split tests added), I4 (preparation-failure and mid-seed factory-throw regressions added), N1 (queued test now awaits the hand-off), N4 (project moved to xUnit v3 and threads `TestContext.Current.CancellationToken`), M2 (named-pool feature now has behavioral coverage), plus the disposed-pool, factory-per-key-caching, and named-pool-options coverage gaps. Production-side resolution is tracked in [[docs/notes/pool{t}.findings.md]].

---

## Important

### I2 — dead config key `PoolOptions:PreparationRequired`

Where: `Startup.cs` (in-memory config dict) and `NamedPoolTests.NamedPool_Registration_Succeeds`.

Issue: both set `PoolOptions:PreparationRequired = "true"`, but `PoolOptions` has no such property; the binder drops the unknown key silently. Preparation is gated by whether a strategy is registered (`isPreparationRequired = preparationStrategy is not null`), not by config. The setup encodes a belief in a switch that does not exist.

Fix: delete the key from both files.

### I3 — brittle assertion on BCL exception text

Where: `PoolTests.Queued_Request_Timesout` and `PoolTests.NeverExceeds_MaxSize`.

Issue: both assert `Assert.Contains("A task was canceled.", exception.Message)`. That string is owned by the .NET BCL and is culture-dependent — it breaks under localization or a framework message change, and it does not prove why the task canceled.

Fix: drop the message assertion; keep `await Assert.ThrowsAsync<TaskCanceledException>(...)`. The original "surface a distinct `TimeoutException`" suggestion is closed — the SemaphoreSlim redesign deliberately keeps `TaskCanceledException` as the v6 contract (see [[docs/notes/semaphoreslim-replaces-pool-lease-rendezvous.md]]), so removing the message check is the whole fix.

### I5 — concurrency test never asserts items are distinct

Where: `PoolTests.Concurrent_Leases_Are_Handled`.

Issue: the test leases 10 items and asserts `ActiveLeases == 10`, but never that the 10 returned instances are distinct. A pool that handed the same object to two leasers would pass. "Do not hand the same item to two callers" is the pool's core guarantee, and it stays unasserted — the newer `Lease_Does_Not_Lose_Wakeups_Under_Concurrency` exercises a single slot, so it does not cover distinctness either.

Fix: `Assert.Equal(10, instances.Distinct().Count());` (reference equality is correct). Optionally assert growth from `MinSize` (5) toward 10.

---

## Moderate

### M1 — magic literals in container-injected tests

Where: `PoolTests.Allocated_Matches_Min` asserts `== 1`; the timeout and max-size tests depend on `MaxSize == 2` and `LeaseTimeout == 10ms` from `Startup`. Tests that build `PoolOptions` inline are self-documenting and exempt; this is only the container-injected facts.

Issue: the test name says "Matches_Min" but the body asserts the literal `1`, with the link to `MinSize` left implicit. Change `Startup` and these break with no obvious cause.

Fix: inject the registered `PoolOptions` singleton and assert against it — `Assert.Equal(options.MinSize, pool.ItemsAllocated)`.

### M3 — `Preparation_Strategy_Is_Applied` asserts indirectly

Where: `PoolTests.Preparation_Strategy_Is_Applied`.

Issue: the test leases, then re-runs `preparationStrategy.IsReadyAsync(instance, ...)`. It observes the strategy's verdict rather than the pool invoking it, so it would pass even if the item arrived ready and `PrepareAsync` was never called — the name overstates what it proves.

Fix: use a spy strategy that counts `PrepareAsync` invocations; assert exactly one call for an unprepared item and zero for an already-ready one.

### M4 — production reads wall-clock; time is not injectable

Where: production — `Pool<TPoolItem>.PoolItem.IdleTime` (`DateTime.UtcNow`) and `EnsurePreparedAsync` (`new CancellationTokenSource(preparationTimeout)`). Tests — `Idle_Timeout_Removes_Items` and the timeout family.

Issue: time cannot be controlled from a test, so idle, lease, and preparation-timeout tests lean on real waits (`Task.Delay`, a real 10ms `LeaseTimeout`) — the classic CI flake. Also flagged production-side in [[docs/notes/pool{t}.findings.md]].

Fix (production change, now unblocked since the refactor landed): inject `TimeProvider` into `Pool<T>` and advance a `FakeTimeProvider` in tests for deterministic idle and timeout coverage.

### M5 — `PoolItemFactory_Doesnt_Crash_On_Dispose` name/assertion mismatch

Where: `PoolItemFactoryTests`.

Issue: the test is named for dispose-safety but its only assertion is `Assert.NotNull(item)`. The behavior under test — disposing the pool and the factory scope does not throw, and the item tolerates double-dispose — is only an implicit no-throw.

Fix: `var ex = Record.Exception(() => { pool.Dispose(); factory.Dispose(); }); Assert.Null(ex);` and assert the item is disposed afterward.

### M6 — near-duplicate timeout tests

Where: `PoolTests.Queued_Request_Timesout` and `PoolTests.NeverExceeds_MaxSize`.

Issue: both lease to `MaxSize` and assert the next lease throws `TaskCanceledException`. `NeverExceeds_MaxSize` now also asserts `ActiveLeases == 2`, but the two still largely prove the same thing.

Fix: split the intents — one proves a queued request times out and is purged (`QueuedLeases` returns to 0 after); the other proves the pool never allocates beyond `MaxSize` (no third item created).

---

## Minor / nits

### N2 — superfluous `Task.Delay(10)`

Where: `PoolTests.Idle_Timeout_Removes_Items`.

Issue: with `IdleTimeout = 0`, the item is evicted on the next dequeue regardless of elapsed time, so the delay implies a timing dependency that is not real.

Fix: remove it, or replace with `FakeTimeProvider` advancement (see M4).

### N3 — typo in test name

Where: `PoolTests.Queued_Request_Timesout`.

Fix: rename to `Queued_Request_Times_Out`.

### N5 — static `ILoggerFactory` never disposed

Where: `PoolTests` and `PoolItemFactoryTests` static fields.

Issue: a disposable held for the process lifetime. Harmless for a test process.

Fix (optional): move to an `IDisposable` fixture if these grow.

### N6 — initial-state facts could consolidate

Where: `PoolTests` — `Pool_Is_Injected`, `Allocated_Matches_Min`, `Available_Matches_Allocated`, `Backlog_Is_Empty`, `Name_Is_TypeName_Dot_Pool`.

Fix (optional): fold into one `Fresh_Pool_Has_Expected_Initial_State` AAA test stating the initial-state contract in one place.

---

## Coverage gaps (owned behavior with no test)

- Constructor null-guards. The ctor runs `ArgumentNullException.ThrowIfNull` on `itemFactory`, `logger`, and `metrics`, but `PoolConstructorTests` covers only the range guards (`MaxSize < 1`, negative `MinSize`) and the factory-throw-mid-seed path. Add an `Assert.Throws<ArgumentNullException>` per null param.
- `Release(null)`. `Release` opens with `ArgumentNullException.ThrowIfNull(item)` — no test exercises it.
- Lease-time factory throw. `Ctor_Disposes_Already_Created_Items_When_Factory_Throws_Mid_Seed` covers a throw during construction, but a throw from `itemFactory.CreateItem()` inside `TryAcquireItem` (on the lease path) has no dedicated test. `LeaseAsync`'s catch releases the permit and `RecordLeaseException` fires, so afterward `ItemsAllocated` and `ActiveLeases` should be unchanged. The shared permit-release path is exercised indirectly by the preparation-failure test; the factory-throw trigger is not.
- Preparation-failure metric. `Preparation_Failure_Discards_Item_And_Serves_Fresh_One` asserts dispose, rethrow, and a fresh item served, but not that `RecordPreparationException` fired. Add a capturing-metrics assertion.
- Metrics counters (the `// todo` in `PoolTests`). The utilization observer is covered by `Utilization_Rate_Observer_Reports_Active_Over_Allocated`; the lease-wait, preparation-time, and exception counters via `MetricCollector<T>` remain untested. Give each metrics test its own `DefaultPoolMetrics` / `IMeterFactory` — `IPoolMetrics` is a singleton and every `Pool<T>` ctor re-registers observers on it, so a shared instance cross-contaminates.
