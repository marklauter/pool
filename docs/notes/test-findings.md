---
title: Unit Test Review — Findings & Suggestions
summary: Review of Pool.Tests for brittle tests, weak/missing assertions, framework-coupling, and behavior-vs-state gaps, with suggested fixes.
tags: [test-review, note]
created: 2026-06-08
---

# Unit Test Review — Findings & Suggestions

**Scope:** `src/Pool.Tests` — `PoolTests.cs`, `NamedPoolTests.cs`, `PoolItemFactoryTests.cs`, plus `Startup.cs` and the `Fakes/`.
**Reviewed against:** `src/Pool` production contracts (`IPool<T>`, `PoolOptions`, the DI extensions, `DefaultPoolMetrics`).

> ⚠️ **Concurrent refactor in progress.** Another agent is refactoring `Pool` while this review was written. Findings tagged **[verify-vs-prod]** assert something about *current* production behavior or reference production line numbers; re-check those against the refactored code before acting. The test-only findings stand regardless.

> **Note on "internal state vs behavior":** the count properties (`ItemsAllocated`, `ItemsAvailable`, `ActiveLeases`, `QueuedLeases`) are part of the **public `IPool<T>` contract**, so asserting on them is testing observable surface, not internal state — that is legitimate and not flagged. The genuine "reaching past behavior" cases are called out individually below.

---

## What's good (keep doing)

- `Lease_And_Release` and `Lease_Queues_Request` are real state-machine tests — they walk lease/release transitions and assert the public counters at each step.
- `Idle_Timeout_Removes_Items` and `Lease_Returns_Ready_Item` assert genuine *behavior* (item disposed on eviction; item connected after lease), not just counts.
- Per-test pool isolation works: `IPool<T>` is registered `Transient` (`ServiceCollectionExtensions.cs:138`), so each test method gets a fresh pool seeded to `MinSize`. The stateful tests are order-independent because of this — worth a guard comment so nobody "optimizes" it to a singleton.

---

## Important

### I1. `Clear_Clears_Pool` contradicts the documented `Clear()` contract **[verify-vs-prod]**
`PoolTests.cs:203-216` asserts `ItemsAllocated == 1` after `Clear()`. But the interface doc on `IPool<T>.Clear` (`IPool{TPoolItem}.cs:11-14`) says *"clears the pool and sets allocated to zero."* The implementation actually **re-seeds to `MinSize`** (`Pool{TPoolItem}.cs:303-313` → `CreateItems` sets `itemsAllocated = count`), so the test is correct about behavior and the **doc is wrong** — or the behavior is wrong and the test is rubber-stamping it.

- **Why it matters:** a test named `Clear_Clears_Pool` that leaves the pool seeded is the canonical "test documents the bug." A future reader trusts the name, not the body.
- **Suggestion:** decide the contract. If re-seed-to-`MinSize` is intended, rename to `Clear_Resets_Pool_To_MinSize`, fix the XML doc, and add an assertion that the *old* items were disposed (Clear calls `EnsureItemsDisposed`). Also add a case for `Clear()` while items are leased — currently only the all-released path is exercised.

### I2. Dead config key `PoolOptions:PreparationRequired` **[verify-vs-prod]**
`Startup.cs:15` and `NamedPoolTests.cs:23` both set `"PoolOptions:PreparationRequired": "true"`, but **`PoolOptions` has no `PreparationRequired` property** (`PoolOptions.cs`). The binder silently drops unknown keys. Preparation is actually gated by whether a strategy is registered (`Pool{TPoolItem}.cs:191` — `isPreparationRequired = preparationStrategy is not null`).

- **Why it matters:** the setup encodes a belief in a switch that doesn't exist. Anyone reading the test config assumes `PreparationRequired` controls something. One-source-of-truth violation between config and type.
- **Suggestion:** delete the key from both places. If a "preparation required" switch *should* exist as an explicit option (rather than being inferred), that's a production design question to raise with the refactor.

### I3. Brittle assertions on BCL exception-message text
`Queued_Request_Timesout` (`PoolTests.cs:101`) and `NeverExceeds_MaxSize` (`PoolTests.cs:125`) both do `Assert.Contains("A task was canceled.", exception.Message)`.

- **Why it matters:** that string is owned by the .NET BCL and is culture-dependent — testing it is testing something we don't own, and it breaks under localization or a framework message change. It also doesn't prove *why* the task canceled (timeout vs. caller cancellation are indistinguishable here).
- **Suggestion:** drop the message assertion; keep `await Assert.ThrowsAsync<TaskCanceledException>(...)`. Better: have the pool surface a distinct `TimeoutException` for lease-timeout so the test can assert the *cause*, not just "something canceled." Today both a 10 ms timeout and a caller-cancelled token produce the same `TaskCanceledException`.

### I4. Missing regression test for the preparation-failure release path **[verify-vs-prod]**
`EnsurePreparedAsync` releases the item back to the pool and records a metric when preparation throws (`Pool{TPoolItem}.cs:452-458`). The recent commit *"fixed resource leak on preparation failure"* shows this path is both important and historically buggy — yet there is **no test** that a preparation failure (a) returns the item to the pool rather than leaking it, (b) rethrows, and (c) records the exception metric.

- **Why it matters:** a fixed bug with no regression test will regress, especially mid-refactor.
- **Suggestion:** add a fake `IPreparationStrategy` whose `PrepareAsync` throws, lease, assert the throw propagates, and assert `ItemsAvailable`/`ItemsAllocated` show the item was returned (no leak). Same gap exists for `TryCreateItem`'s decrement-on-throw path (`Pool{TPoolItem}.cs:368-377`): a factory that throws should leave `ItemsAllocated` unchanged.

### I5. `Concurrent_Leases_Are_Handled` never asserts the pool's core invariant
`PoolTests.cs:168-201` asserts 10 leases succeed and `ActiveLeases == 10`, but never that the **10 returned instances are distinct**. A pool that handed the same object to two leasers would pass this test.

- **Why it matters:** "don't hand the same item to two callers" is the single most important guarantee of a pool, and the only concurrency test doesn't check it.
- **Suggestion:** `Assert.Equal(10, instances.Distinct().Count());` (reference equality is correct here). Consider also asserting growth from `MinSize` (5) on the way to 10.

---

## Moderate

### M1. Magic numbers coupled to `Startup` config
`Allocated_Matches_Min` asserts `== 1` (`PoolTests.cs:21`); `NeverExceeds_MaxSize` and `Queued_Request_Timesout` depend on `MaxSize == 2`; the timeout tests depend on `LeaseTimeout == 10ms`. All of these literals live in `Startup.cs:11-20`, invisible from the test body.

- **Why it matters:** the test name says "Matches_Min" but the assertion says "equals 1" — the link to `MinSize` is implicit. Change `Startup` and these break with no obvious cause.
- **Suggestion:** inject `PoolOptions` into the test class (it's a registered singleton) and assert against it: `Assert.Equal(options.MinSize, pool.ItemsAllocated)`. Now the test states its intent and survives config changes.

### M2. `Pool_Is_Injected` and `NamedPool_Registration_Succeeds` test the DI container, not us
`Pool_Is_Injected` (`PoolTests.cs:18`) is `Assert.NotNull(pool)` — that only proves MS DI + Xunit.DependencyInjection resolved a registration. `NamedPool_Registration_Succeeds` (`NamedPoolTests.cs:15-39`) is the same shape: it asserts `client.Pool` is non-null and stops there.

- **Why it matters:** these pass even if the pool is completely broken. The named-pool feature's *whole point* — isolation by key — is untested: nothing verifies that two clients get two different pools, or that a named pool actually leases.
- **Suggestion:** make them behavioral. For the named pool: register two clients, assert their pools are different instances, lease from each, and assert options were bound (e.g., `MaxSize`). At minimum, lease one item and assert it's usable. Keep one thin "does it wire up" smoke test if you like, but it shouldn't be the only coverage.

### M3. `Preparation_Strategy_Is_Applied` asserts indirectly and can't tell "prepared" from "already ready"
`PoolTests.cs:149-166` leases, then asserts `await preparationStrategy.IsReadyAsync(instance, ...)`. It re-runs the strategy instead of observing that the pool *invoked* it, and it overlaps `Lease_Returns_Ready_Item` (which already asserts `IsConnected`).

- **Why it matters:** the test name claims the strategy was *applied*, but it would also pass if the item arrived ready and `PrepareAsync` was never called.
- **Suggestion:** use a spy strategy that counts `PrepareAsync` invocations; assert it was called exactly once for an unprepared item, and zero times for an already-ready one. That actually tests "strategy is applied."

### M4. Time-based tests are flaky-by-construction; production isn't time-injectable
`Idle_Timeout_Removes_Items` uses `Task.Delay(10)` and the timeout tests lean on a real 10 ms `LeaseTimeout`. Production reads wall-clock directly (`PoolItem.IdleTime` → `DateTime.UtcNow`, `new CancellationTokenSource(timeout)`), so timing can't be controlled from a test.

- **Why it matters:** real-time waits are the classic CI flake; under load a 10 ms timeout can fire early or a delay can be too short. The writing-csharp guidance explicitly prefers `TimeProvider` over `DateTime.UtcNow`/`Stopwatch` for exactly this reason.
- **Suggestion (production change, coordinate with the refactor):** inject `TimeProvider` into `Pool<T>` and use `FakeTimeProvider` in tests to advance time deterministically — no `Task.Delay`, no wall-clock dependence. This unlocks reliable idle/lease/preparation-timeout tests.

### M5. `PoolItemFactory_Doesnt_Crash_On_Dispose` — name/assertion mismatch
`PoolItemFactoryTests.cs:19-42` is named for dispose-safety but its only assertion is `Assert.NotNull(item)` (`:33`). The actual thing under test — "disposing the pool *and* the factory scope doesn't throw, and the item tolerates double-dispose" — is only an implicit no-throw.

- **Suggestion:** make it explicit: `var ex = Record.Exception(() => { pool.Dispose(); factory.Dispose(); }); Assert.Null(ex);` and assert the item is disposed afterward (`Assert.True(((Echo)item).IsDisposed())`). That verifies the double-dispose-safety the comments describe.

### M6. `Queued_Request_Timesout` and `NeverExceeds_MaxSize` are near-duplicates
Both lease to `MaxSize` (2) and assert the next lease throws `TaskCanceledException` with the same message. The only real difference is `Queued_Request_Timesout` releases in a `finally`.

- **Suggestion:** collapse to two *distinct* intents: one proves "a queued request times out and is purged from `QueuedLeases`" (assert `QueuedLeases` returns to 0 after), the other proves "the pool never allocates beyond `MaxSize`" (assert `ItemsAllocated == MaxSize` and that no 3rd item was created). As written they prove the same thing twice.

---

## Minor / Nits

- **N1. `Lease_Queues_Request` depends on synchronous TCS continuation.** `Assert.True(task.IsCompleted)` immediately after `Release` (`PoolTests.cs:64`) only holds because `TaskCompletionSource.TrySetResult` runs continuations synchronously. If the TCS is ever switched to `RunContinuationsAsynchronously` (a plausible refactor for deadlock-safety), this flakes. Prefer `await task` with a short timeout over asserting synchronous completion.
- **N2. Superfluous `Task.Delay(10)`** in `Idle_Timeout_Removes_Items` (`:143`). With `IdleTimeout = 0`, `IdleTime < 0ms` is always false, so the item is evicted on the next dequeue regardless of elapsed time. The delay implies a timing dependency that isn't real — remove it or replace with `FakeTimeProvider` advancement (see M4).
- **N3. Typo** in test name `Queued_Request_Timesout` → `Times_Out` (`:91`).
- **N4. `TestContext.Current.CancellationToken` not propagated.** Async tests pass `CancellationToken.None`. The writing-csharp guidance wants the runner's token threaded through. *Blocked:* that API is xUnit **v3**; this project is on xUnit **v2.9.3** (`Pool.Tests.csproj`). Note it as part of a future v3 upgrade rather than an immediate fix.
- **N5. Static `ILoggerFactory` is never disposed** (`PoolTests.cs:12`, `PoolItemFactoryTests.cs:12`). Harmless for a test process, but it's a disposable held for the process lifetime; consider `IDisposable` test fixtures if this grows.
- **N6. The first four facts** (`Pool_Is_Injected`, `Allocated_Matches_Min`, `Available_Matches_Allocated`, `Backlog_Is_Empty`) could consolidate into one `Fresh_Pool_Has_Expected_Initial_State` AAA test, reducing four pool resolutions to one and stating the initial-state contract in one place.

---

## Coverage gaps (owned behavior with no test)

| Behavior | Production location | Suggested test |
|---|---|---|
| Preparation failure returns item + records metric + rethrows | `Pool{TPoolItem}.cs:452-458` | Throwing prep strategy → assert no leak, metric recorded, rethrow (**I4**) |
| Factory throw decrements `ItemsAllocated` | `Pool{TPoolItem}.cs:368-377` | Throwing factory → `ItemsAllocated` unchanged after |
| `LeaseAsync`/`Clear` on disposed pool throw `ObjectDisposedException` | `Pool{TPoolItem}.cs:236,307,476` | Dispose then lease → assert throw |
| Constructor null-guards (`itemFactory`/`logger`/`metrics`) | `Pool{TPoolItem}.cs:177-179` | `Assert.Throws<ArgumentNullException>` per param (boundary tests) |
| `Release(null)` throws | `Pool{TPoolItem}.cs:277` | `Assert.Throws<ArgumentNullException>` |
| Concurrent leases are distinct instances | — | `instances.Distinct().Count() == 10` (**I5**) |
| `PoolFactory.CreatePool` caches per key | `PoolFactory.cs:21-25` | Same key → same instance; unknown key → resolves/throws as documented |
| Named-pool isolation by key | `NamedPoolServiceCollectionExtensions.cs` | Two clients → two pools, options bound (**M2**) |
| Metrics recorded (the `// todo` at `PoolTests.cs:8`) | `DefaultPoolMetrics.cs` | Use `MetricCollector<T>` to assert lease-wait/prep-time/exception counters |

> **Metrics-test caveat:** `IPoolMetrics` is registered as a **singleton** (`AddDefaultPoolMetrics` → `TryAddSingleton`), and every `Pool<T>` constructor re-registers observers on it (`Pool{TPoolItem}.cs:185-189`), each call creating a fresh observable instrument on the shared meter. When you add the metrics tests from the `todo`, give each test its own `DefaultPoolMetrics`/`IMeterFactory` so observer overwrites and duplicate-instrument registration don't cross-contaminate.

---

## Suggested priority order

1. **I1** (Clear contract) and **I2** (dead config) — cheap, and they remove actively-misleading signal. Coordinate with the refactor.
2. **I4** (preparation-failure regression) — protects a just-fixed bug during an active refactor.
3. **I5** + **I3** — strengthen the concurrency invariant and de-brittle the timeout assertions.
4. **M4 / TimeProvider** — the structural fix that makes the whole timeout/idle family deterministic; best done as part of the refactor rather than after.
5. The remaining **M**/**N** items as follow-ups.
