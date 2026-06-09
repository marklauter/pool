---
title: Smtp.Pool — completion report
summary: What it would take to bring the resurrected samples/Smtp.Pool from a compiling skeleton to a near production-grade MailKit SMTP connection pool. Covers correctness bugs in the restored prepare path, the missing send surface, auth/security gaps, resilience, observability, testing, and the Pool-library hooks the SMTP case demands.
tags: [smtp-pool, sample, report, note]
created: 2026-06-09
aliases: []
document.status: draft
---

# Smtp.Pool — completion report

## What landed (baseline)

The sample was not merely out of the solution — it was deleted in `defd6cf` ("removed incomplete sample app"). It is restored from history into **`samples/Smtp.Pool/`** (sibling to `src` and `tests`), modernized to the repo's conventions, and building clean:

- **CPM**: `MailKit 4.17.0` pinned in `Directory.Packages.props`; the csproj carries a version-less `<PackageReference Include="MailKit" />`.
- **CBP**: no local target framework / analyzer settings — everything inherits `Directory.Build.props` (net10, nullable, `AnalysisMode=all`, `TreatWarningsAsErrors`). `IsPackable=false` (a sample, not a package).
- **Solution**: added to `Pool.slnx` between `Pool` and `Pool.Tests`.
- **Build**: `dotnet build Pool.slnx` → 0 warnings / 0 errors; `dotnet format --verify-no-changes` clean.

The sample was first restored as a minimal `IPool<IMailTransport>` skeleton, then evolved into the real design below. The pool now leases a **`SmtpConnection`** wrapper, not a raw transport — a stable pooled identity around a **replaceable** `IMailTransport`. That one move is what makes per-connection lifetime limits and recycling expressible (see §4) without changing the `Pool` library.

Current shape (all `internal sealed` behind the public `AddSmtpClientPool` extension, except the public `SmtpConnection` lease type and the options):

- `SmtpHostOptions` — endpoint + TLS: `Host`/`Port`/`Security` (`SecureSocketOptions`) + certificate policy, validated.
- `SmtpClientCredentials` — basic-auth user/password, validated.
- `SmtpClientOptions` — socket timeout + the three recycle limits (`MaxConnectionLifetime`, `MaxIdleLifetime`, `MaxMessagesPerConnection`) and the `ProbeAfter` NOOP-throttle window.
- `SmtpConnection : IDisposable` (public) — wraps a replaceable transport; tracks age / idle / message-count off an injected `TimeProvider`; `ShouldRecycle(now)` is the "whichever comes first" decision; exposes `SendAsync(MimeMessage)`; recycles and gracefully QUITs.
- `SmtpConnectionFactory : IItemFactory<SmtpConnection>` — pure (no I/O) construction; builds configured transports (timeout + cert policy).
- `SmtpConnectionPreparationStrategy : IPreparationStrategy<SmtpConnection>` — ages out / reconnects in the preparation step.
- `SmtpClientPoolServiceCollectionExtensions.AddSmtpClientPool(...)` — DI wiring (`AddPoolItemFactory`, `AddPreparationStrategy`, `AddPool<SmtpConnection>`, `TimeProvider`).

Implementations are exposed to the test project via `InternalsVisibleTo` (mirroring `Pool` → `Pool.Tests`); the CA1812 false positive (the analyzer can't see DI instantiation) is suppressed inline with justification.

**Is a pool a good fit here?** Yes — though to be accurate about *why*. MailKit's `SmtpClient` is reusable across many messages (jstedfast explicitly encourages connection reuse to amortize the TCP + TLS + AUTH handshake), but `Send`/`SendAsync` are **not thread-safe**: you must not run two operations on one instance concurrently. MailKit even exposes a `SyncRoot` so callers can serialize access. The documented ways to get that serialization are (1) one instance shared with `lock (client.SyncRoot)`, or (2) one instance per thread. A pool is a third valid approach: an exclusive lease gives one caller sole ownership for the operation's duration (the serialization `Send` needs) *and* recycles connections across callers (the reuse jstedfast recommends) — which is the combination the per-message-throughput case wants.

---

## Gap analysis

### 1. Correctness of the prepare path

- **Half-open reconnect.** *(Landed.)* `SmtpConnectionPreparationStrategy.PrepareAsync` recycles an aged-out connection, else `DisconnectAsync(quit: false)` on a still-`IsConnected` (half-open) one before reconnecting — so it never calls `ConnectAsync` on an already-connected client (which MailKit throws on). The `PrepareAsync_Disconnects_Half_Open_Before_Reconnecting` test pins it.
- **Graceful QUIT.** *(Landed, at the wrapper.)* `SmtpConnection.Dispose()` and `RecycleAsync` do a best-effort `Disconnect(quit: true)` before disposing the transport, so the server sees a clean QUIT rather than a dropped socket. An async teardown hook in the Pool library would still be cleaner (sync `Disconnect` in `Dispose` blocks the pool's dispose path) — see §7.
- **Auth still assumed mandatory.** `PrepareAsync` always authenticates; an internal relay / port-25 MTA that wants no auth would fail. Still to do: make auth conditional (ties into the OAuth/`ISmtpAuthenticator` work in §3).

### 2. The send surface

- **A send path exists.** *(Landed.)* **MimeKit** is on CPM, and `SmtpConnection.SendAsync(MimeMessage, CancellationToken)` sends over the leased connection and counts the message toward the recycle limit. A consumer injects `IPool<SmtpConnection>`, leases, sends, and releases (the README's own pattern).
- **Typed client + lease scope.** *(Still to do.)* A `PooledSmtpSender` that leases/sends/releases in a `finally`, plus an `await using` lease-scope guard, so a forgotten manual `Release` can't permanently shrink the pool. Register via the `AddPool<SmtpConnection, PooledSmtpSender>` overload.
- **Send-result handling.** *(Still to do.)* Catch `SmtpCommandException` (inspect `StatusCode` / `ErrorCode`) and `SmtpProtocolException`; classify 4xx (transient, retry) vs 5xx (permanent, fail) vs auth errors (recycle), and flag the connection broken so the next `IsReadyAsync` reconnects.

### 3. Security & authentication

- **OAuth2 / modern auth.** Basic username+password is disabled by Google and Microsoft 365. Production needs `SaslMechanismOAuth2` with token acquisition + refresh; `SmtpClientCredentials` models basic auth only.
- **TLS policy.** *(Landed.)* `SmtpHostOptions` now carries `Security` (`SecureSocketOptions`, default `StartTls` on port 587) instead of a `UseSsl` bool, plus `RequireValidCertificate` / `CheckCertificateRevocation`. The factory is secure by default and only installs an accept-all `ServerCertificateValidationCallback` when validation is explicitly opted out (gated, with a justified CA5359 suppression). What remains: a *custom* validation callback (pinning / specific-thumbprint trust) rather than the all-or-nothing toggle.
- **Secret handling.** Credentials in configuration are fine for a sample; production should bind from a secret store (Key Vault, user-secrets, env). Never log `AUTH` — MailKit's `ProtocolLogger` must redact.

### 4. Reliability & resilience

- **Connection recycling.** *(Landed.)* `SmtpConnection` tracks the three limits real servers enforce — total age, idle time, and messages sent — and `ShouldRecycle` ages out on whichever comes first; `SmtpConnectionPreparationStrategy` reconnects a fresh transport on the next lease. Each limit has deterministic `FakeTimeProvider` coverage. Limits are configurable per `SmtpClientOptions` and individually disablable (zero = no limit).
- **NOOP probe cost.** *(Landed.)* `IsReadyAsync` skips the NOOP round-trip when the connection was used within `ProbeAfter`, and only probes a connection that has been idle longer — so the common hot-path lease pays no extra round-trip.
- **Transient-fault retry.** *(Still to do.)* Wrap connect/auth (and send) in Polly retry-with-backoff; respect SMTP greylisting (4xx) by retrying, but never retry hard 5xx. A connection-level failure before the `250` is safe to retry on a fresh connection; a 5xx command rejection is not.
- **Backpressure.** *(Still to do.)* Set a finite `PoolOptions.LeaseTimeout` and surface saturation clearly instead of waiting forever (the default is infinite).

### 5. Observability

- The Pool already emits `lease_wait_time`, `item_preparation_time`, `lease_exception`, `preparation_exception` via `DefaultPoolMetrics`. Add **app-level** SMTP metrics: send latency, send outcome by SMTP status class, connect/auth duration, auth-failure count.
- Structured logging around prepare/connect/auth/send with credential redaction; optional opt-in `ProtocolLogger` for wire-level debugging.
- Wire both into OpenTelemetry.

### 6. Testing

A suite has **landed** in `tests/Smtp.Pool.Tests` (41 tests, xUnit v3 + NSubstitute + ArchUnitNET + `FakeTimeProvider`), green over the `Smtp.Pool` assembly:

- `SmtpConnection` — each recycle limit isolated and proven deterministically (messages, total age, idle), "whichever comes first", idle-clock reset on activity, `RecycleAsync` quits+disposes+replaces the transport, graceful-QUIT `Dispose`, NOOP ping success/failure, and ctor null-guards — all against a faked `IMailTransport` + `FakeTimeProvider`.
- `SmtpConnectionPreparationStrategy` — the `IsReadyAsync` truth table (not-connected / not-authenticated / aged-out / probe-when-idle / probe-fails / ready-recently-used) and `PrepareAsync` fresh-connect, half-open-disconnect, and aged-out-recycle paths.
- `SmtpConnectionFactory` — returns a connection, the transport carries the configured timeout, certificate policy secure-by-default and relaxed-when-opted-out, ctor null-guards.
- `AddSmtpClientPool` — registers pool/factory/strategy/`TimeProvider`, honors the configure override, null-guards.
- **Architecture** (`Architecture/ArchitectureTests.cs`, mirroring `Pool.Tests`) — types stay in the `Smtp.Pool` tree, concrete classes are sealed, no public instance fields, the `IItemFactory`/`IPreparationStrategy` implementations stay internal, and the sample takes no ASP.NET Core dependency.

Coverage is scoped to `[Smtp.Pool]*` with the ratchet started at `0,0,0` (raise as the sample grows). NSubstitute, ArchUnitNET, and `Microsoft.Extensions.TimeProvider.Testing` are referenced via CPM; the test-naming and `CA1515` (public test classes) carve-outs are centralized in the `.Tests`-gated section of `Directory.Build.props`. Still to do:

- **Integration**: against a containerized SMTP sink — `smtp4dev`, `Papercut`, or `MailHog` via `Testcontainers` — proving real connect/auth/send and reconnect-after-drop.
- **Concurrency**: prove exclusive use (no connection handed to two leasers mid-send) under parallel load.

### 7. Pool-library hooks the SMTP case surfaces (most valuable findings)

The SMTP case pushed on three areas of the **Pool library**. The `SmtpConnection` wrapper mitigates all three at the sample level, but each would be cleaner as a first-class library feature reusable by any connection-oriented resource (SMTP, DB, gRPC):

1. **No item teardown/destroy hook.** The pool disposes items via synchronous `IDisposable.Dispose()`; there is no async teardown. *Mitigation:* the wrapper does a best-effort sync `Disconnect(quit: true)` in `Dispose`. *Library win:* a `ValueTask DestroyAsync(TPoolItem)` (or `IDisposalStrategy<T>`) would allow an async graceful QUIT without blocking the dispose path.
2. **No recycle policy (max uses / max lifetime).** *Mitigation:* the wrapper tracks age/idle/count and the strategy ages items out in `IsReadyAsync`/`PrepareAsync`. *Library win:* a built-in per-item use-count + max-age policy would spare every pooled-resource author from re-implementing it.
3. **Probe-on-every-lease.** *Mitigation:* the strategy throttles its own NOOP via `ProbeAfter`. *Library win:* a configurable readiness-throttle window would standardize it.

The wrapper is the forcing function that makes these concrete — each is arguably worth its own issue against the Pool library.

---

## Prioritized roadmap

| Phase | Scope | Effort |
|---|---|---|
| Phase | Scope | Status |
|---|---|---|
| **P0 — Correctness** | `PrepareAsync` reconnect guards, graceful QUIT (§1) | **Landed** (auth-optional remains) |
| **P1 — Send path** | MimeKit + `SmtpConnection.SendAsync` (§2) | **Landed**; typed sender + lease scope + error classification remain (~1 day) |
| **P2 — Recycling** | Three-limit wrapper + age-out strategy, `TimeProvider` (§4) | **Landed** |
| **P3 — Tests** | Wrapper/strategy/factory unit suite, 41 tests (§6) | **Landed**; integration (smtp4dev) + concurrency remain (~1 day) |
| **P4 — Security** | `SecureSocketOptions` + cert policy (§3) | **Landed**; OAuth2 SASL + secret-store + custom cert callback remain (~1.5 days) |
| **P5 — Resilience** | Polly retry, finite lease timeout (§4) | To do (~1 day) |
| **P6 — Observability** | App-level SMTP metrics + structured logging + OTel (§5) | To do (~0.5 day) |
| **P7 — Library hooks** | Teardown hook + recycle policy + probe-throttle in the Pool library (§7) | To do (~2 days; sample already mitigates) |

The sample is now a correct, tested, genuinely usable connection pool with real lifetime management. The remaining work (typed sender, OAuth2, retry, observability, integration tests) makes it production-credible; the library hooks are the deeper reusable wins the SMTP case justifies.
