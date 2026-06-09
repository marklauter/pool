---
title: Pool.Tests review — findings
summary: Test-review findings for tests/Pool.Tests after the SemaphoreSlim refactor. All important, moderate, and coverage-gap items are implemented and verified; two cosmetic nits (N5, N6) are deferred. Production-side status in pool{t}.findings.md.
tags: [test-review, pool, note]
created: 2026-06-08
aliases: []
document.status: resolved
---

# Pool.Tests review — findings

The test-review findings for `tests/Pool.Tests`, resolved against current code after the SemaphoreSlim redesign and the M4 `TimeProvider` work both landed. Finding IDs are preserved from the original triage. Production-side status: [[docs/notes/pool{t}.findings.md]].

Verification: `dotnet test` green (80 tests); coverage 100/92.85/100 line/branch/method over the 95/90/95 ratchet; `dotnet format --verify-no-changes` clean.

## Resolved

- I2 — deleted the dead `PoolOptions:PreparationRequired` key from `Startup.cs` and `NamedPoolTests.cs`.
- I3 — dropped the culture-dependent `Assert.Contains("A task was canceled.")` from `Queued_Request_Times_Out`, `NeverExceeds_MaxSize`, and `Lease_Times_Out_Deterministically_When_Capacity_Stays_Saturated`; the `ThrowsAsync<TaskCanceledException>` assertion stands.
- I5 — `Concurrent_Leases_Are_Handled` asserts `instances.Distinct().Count() == 10` (no item handed to two leasers).
- M1 — `PoolTests` injects the registered `PoolOptions` (`configuredOptions`); `Allocated_Matches_Min` and the cap assertions read `MinSize`/`MaxSize` instead of literals.
- M3 — `Preparation_Strategy_Is_Applied` uses `CountingEchoPreparationStrategy` to assert `PrepareAsync` ran once for an unprepared item and zero times for an already-ready one.
- M4 — resolved by the parallel session: idle, lease, and preparation timeouts are all `TimeProvider`/CTS-driven, with deterministic `FakeTimeProvider` tests. See [[docs/notes/pool{t}.findings.md]] and [[docs/notes/semaphoreslim-replaces-pool-lease-rendezvous.md]].
- M5 — `PoolItemFactory_Doesnt_Crash_On_Dispose` asserts no-throw via `Record.Exception` and that the item is disposed.
- M6 — the two real-wait timeout tests now prove distinct intents: `Queued_Request_Times_Out` asserts the backlog is purged (`QueuedLeases == 0`); `NeverExceeds_MaxSize` asserts the cap held (`ItemsAllocated == MaxSize`).
- N2 — removed the superfluous `Task.Delay(10)` from `Idle_Timeout_Removes_Items` (eviction is elapsed-time-independent at `IdleTimeout = 0`).
- N3 — renamed `Queued_Request_Timesout` → `Queued_Request_Times_Out`.

Coverage gaps closed:

- Constructor null guards — `Ctor_Null_ItemFactory_Throws`, `Ctor_Null_Logger_Throws`, `Ctor_Null_Metrics_Throws` in `PoolConstructorTests`.
- `Release(null)` — `Release_Null_Throws` in `PoolTests`.
- Lease-time factory throw — `Lease_Releases_Permit_When_Factory_Throws` asserts the permit is released and counts stay unchanged.
- Metrics — new `MetricsTests` uses `MetricCollector<T>` (per-test `IMeterFactory` + `DefaultPoolMetrics`) to assert the `lease_wait_time`, `item_preparation_time`, `lease_exception`, and `preparation_exception` instruments. The `// todo` is gone and the preparation-failure metric gap is covered.

## Deferred — cosmetic

- N5 — the static `ILoggerFactory` in `PoolTests`/`PoolItemFactoryTests` is never disposed. Harmless for a test process; closing it would mean an `IDisposable` fixture. Left as-is.
- N6 — the five initial-state facts (`Pool_Is_Injected`, `Allocated_Matches_Min`, `Available_Matches_Allocated`, `Backlog_Is_Empty`, `Name_Is_TypeName_Dot_Pool`) could fold into one `Fresh_Pool_Has_Expected_Initial_State`. Doing so reduces per-fact failure granularity; left as separate facts.
