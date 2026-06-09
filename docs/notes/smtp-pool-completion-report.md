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

**Restore decision (flag for review):** the last committed state (`32f2d9c`) had no `IItemFactory` — it had been dropped as "dead code" one commit earlier — so a faithful 3-class restore cannot compile under the strict ruleset (CA1812, and a pool with no factory can't construct clients). I restored the last state **plus** the minimum coherent wiring to make it a real, buildable skeleton:

- `SmtpHostOptions`, `SmtpClientCredentials` — restored, with `[Required]`/`[Range]` validation.
- `SmtpClientOptions` — restored (was empty); now carries a used `TimeoutMilliseconds`.
- `SmtpClientFactory : IItemFactory<IMailTransport>` — re-added (creates a configured `SmtpClient`).
- `SmtpClientPreparationStrategy : IPreparationStrategy<IMailTransport>` — the split version from `32f2d9c`, options-bound.
- `SmtpClientPoolServiceCollectionExtensions.AddSmtpClientPool(...)` — DI wiring against the **current** API (`AddPoolItemFactory`, `AddPreparationStrategy`, `AddPool<IMailTransport>`).

The factory/strategy are `public` (a sample's implementation should be visible; also sidesteps CA1812). If you wanted a byte-faithful restore with the gaps left broken instead, say so and I'll swap.

**Is a pool even the right pattern here?** Yes, decisively. MailKit's `SmtpClient` is **not thread-safe** — one instance may only run one operation at a time. The pool's exclusive-lease model gives each caller sole ownership of a connected, authenticated client for the duration of a lease, which is exactly the discipline `SmtpClient` requires. Connection reuse also amortizes the TCP + TLS + AUTH handshake, which is the dominant per-send cost.

---

## Gap analysis

### 1. Correctness bugs in the restored prepare path (must-fix)

These exist in the code as restored — they were latent in the original WIP and are real:

- **Double-connect / double-auth throw.** `PrepareAsync` calls `ConnectAsync` then `AuthenticateAsync` unconditionally. MailKit throws `InvalidOperationException` if the client is **already connected** or **already authenticated**. This fires whenever `IsReadyAsync` returns false on a *half-open* client (e.g. `IsConnected == true` but the NOOP probe failed): the pool then calls `PrepareAsync`, which re-connects an already-connected client and throws. Fix: guard — `if (item.IsConnected) await item.DisconnectAsync(quit: false, ct);` before connecting, and only authenticate when `!item.IsAuthenticated`.
- **No graceful QUIT on eviction.** The pool disposes idle/evicted items via `IDisposable.Dispose()`. `SmtpClient.Dispose()` does **not** send SMTP `QUIT`; the server sees an abrupt socket drop. Production wants `DisconnectAsync(quit: true)` before disposal — but the Pool API has no teardown hook to do this (see §7).
- **Auth assumed mandatory.** `PrepareAsync` always authenticates; an internal relay / port-25 MTA that wants no auth would fail. Make auth conditional on credentials being present.

### 2. The send surface is entirely missing (headline gap)

The sample pools and *prepares* transports but offers no way to actually send mail. A near-production sample needs:

- A **MimeKit** dependency (`MimeMessage` is the unit you send; add to CPM).
- A typed client — e.g. `PooledSmtpSender` — exposing `Task SendAsync(MimeMessage, CancellationToken)` that leases a client, sends, and releases in a `finally`. Register it with the named-pool overload `AddPool<IMailTransport, PooledSmtpSender>(...)`.
- A **lease scope guard** (`await using` handle that auto-releases). `IPool.Release` is manual; a single forgotten release permanently shrinks the pool. A disposable lease handle is the safest consumer ergonomic.
- Send-result handling: catch `SmtpCommandException` (inspect `StatusCode` / `ErrorCode`) and `SmtpProtocolException`; classify 4xx (transient, retry) vs 5xx (permanent, fail) vs auth errors (recycle).

### 3. Security & authentication

- **OAuth2 / modern auth.** Basic username+password is disabled by Google and Microsoft 365. Production needs `SaslMechanismOAuth2` with token acquisition + refresh; `SmtpClientCredentials` models basic auth only.
- **TLS policy.** `ConnectAsync(host, port, bool useSsl, ...)` is coarse. Expose `SecureSocketOptions` (esp. `StartTls` for submission port 587) and a `ServerCertificateValidationCallback` (don't silently accept invalid certs in production).
- **Secret handling.** Credentials in configuration are fine for a sample; production should bind from a secret store (Key Vault, user-secrets, env). Never log `AUTH` — MailKit's `ProtocolLogger` must redact.

### 4. Reliability & resilience

- **Transient-fault retry.** Wrap connect/auth (and send) in Polly retry-with-backoff; respect SMTP greylisting (4xx) by retrying, but never retry hard 5xx.
- **Connection recycling.** Providers cap messages-per-connection and connection age. Need a "max uses / max lifetime then recycle" policy. The Pool has no per-item use-count or lifetime cap today (see §7).
- **NOOP probe cost.** `IsReadyAsync` does a NOOP round-trip on **every** lease — correct for liveness, but it adds latency under high throughput. Consider making the probe optional or skipping it when the client was used within the last N seconds, leaning on send-time reconnect instead.
- **Backpressure.** Set a finite `PoolOptions.LeaseTimeout` and surface saturation clearly instead of waiting forever (the default is infinite).

### 5. Observability

- The Pool already emits `lease_wait_time`, `item_preparation_time`, `lease_exception`, `preparation_exception` via `DefaultPoolMetrics`. Add **app-level** SMTP metrics: send latency, send outcome by SMTP status class, connect/auth duration, auth-failure count.
- Structured logging around prepare/connect/auth/send with credential redaction; optional opt-in `ProtocolLogger` for wire-level debugging.
- Wire both into OpenTelemetry.

### 6. Testing (none exists)

- **Unit**: `SmtpClientFactory` applies `TimeoutMilliseconds`; `SmtpClientPreparationStrategy` ready/not-ready/reconnect-guard logic against a faked `IMailTransport` (NSubstitute) — including the half-open re-prepare path from §1.
- **Integration**: against a containerized SMTP sink — `smtp4dev`, `Papercut`, or `MailHog` via `Testcontainers` — proving real connect/auth/send and reconnect-after-drop.
- **Concurrency**: prove exclusive use (no client handed to two leasers mid-send) under parallel load.
- Coverage today is scoped to `[Pool]*` and keyed off `.Tests` projects, so the sample is excluded — a `Smtp.Pool.Tests` project would need its own include/threshold wiring.

### 7. Pool-library hooks the SMTP case surfaces (most valuable findings)

The SMTP use case exposes three genuine gaps in the **Pool library** itself, not just the sample:

1. **No item teardown/destroy hook.** `IItemFactory` creates; nothing on the factory/strategy runs at eviction. There is no way to gracefully `DisconnectAsync(quit: true)` an SMTP client before the pool disposes it. A `ValueTask DestroyAsync(TPoolItem)` (or an `IDisposalStrategy<T>`) would close this for any connection-oriented resource (SMTP, DB, gRPC).
2. **No recycle policy (max uses / max lifetime).** Cannot honor server message-per-connection caps or rotate aged connections. A per-item use counter + max-age, checked at release/lease, would be reusable across all pooled resources.
3. **Probe-on-every-lease is unconditional.** `IsReadyAsync` always runs; no throttle/skip window. Fine for the library to keep it simple, but worth a configurable hook for latency-sensitive callers.

These are arguably worth their own issues against the Pool library — the SMTP sample is the forcing function that makes the need concrete.

---

## Prioritized roadmap

| Phase | Scope | Effort |
|---|---|---|
| **P0 — Correctness** | Fix `PrepareAsync` reconnect guards (§1); make auth optional | ~0.5 day |
| **P1 — Make it send** | MimeKit dep, `PooledSmtpSender` typed client, lease-scope guard, send-error classification (§2) | ~1 day |
| **P2 — Tests** | Unit (faked transport) + integration (smtp4dev/Testcontainers) + concurrency (§6) | ~1.5 days |
| **P3 — Security** | `SecureSocketOptions` + cert callback; OAuth2 SASL; secret-store binding (§3) | ~1.5 days |
| **P4 — Resilience** | Polly retry, connection recycling, finite lease timeout, NOOP-probe tuning (§4) | ~1 day |
| **P5 — Observability** | App-level SMTP metrics + structured logging + OTel (§5) | ~0.5 day |
| **P6 — Library hooks** | Teardown hook + recycle policy in the Pool library, then graceful QUIT in the sample (§7) | ~2 days (library + sample) |

P0–P2 yield a correct, tested, genuinely usable sample. P3–P5 make it production-credible. P6 is the deeper library work the SMTP case justifies.
