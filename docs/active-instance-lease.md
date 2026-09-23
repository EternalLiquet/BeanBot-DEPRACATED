# Active-instance lease

BeanBot uses MongoDB to ensure that only one process using a given Discord bot identity can admit commands, event handlers, or background work at a time. The lease key is the authenticated Discord bot user ID. A random per-process holder ID is stored only as fencing metadata and is never written to normal logs or health responses.

## Startup and overlap

The health endpoint starts first, then BeanBot logs in to Discord so it can learn the stable bot user ID. Before gateway recovery, commands, event handlers, or background services are enabled, the process must atomically acquire the MongoDB lease for that identity. An unexpired lease owned by another process fails startup closed rather than waiting indefinitely or allowing both processes to become active.

This means normal rolling deployments should avoid deliberately overlapping two healthy BeanBot processes with the same token. If overlap happens anyway, the existing holder remains active and the newcomer exits. A supervisor may retry the newcomer after the previous holder shuts down or its lease expires.

## Renewal, fencing, and takeover

The default lease lifetime is 45 seconds. The active process starts a single-flight renewal every 15 seconds and treats the final 10 seconds of the lease as a safety margin. MongoDB acquisition, renewal, reconciliation, and release waits are application-bounded.

If a write has an ambiguous outcome, BeanBot does not assume success and does not issue a blind duplicate mutation. It performs an exact read for the bot identity and accepts ownership only when the stored holder ID matches this process and the lease is still unexpired. If renewal ownership is lost, or ownership cannot be proven before the safety margin begins, BeanBot marks readiness unhealthy and requests application shutdown before the known lease can expire.

An expired lease may be taken over atomically by another holder. Renewal and release require an exact holder match, so an old process cannot extend or delete a successor's lease after fencing. If graceful release cannot be confirmed, the lease is left to expire rather than risking deletion of another holder.

Under normal graceful shutdown, BeanBot first closes and drains command/event/background admission, flushes owner alerts, then releases the lease before stopping the health listener and Discord client. If side-effecting Discord work does not drain safely, explicit lease release is skipped so another process cannot become active while the old process may still own Discord operations; process exit and lease expiry provide the handoff instead.

## Health and monitoring

`/healthz` returns `200 OK` only when Discord is ready, MongoDB is reachable, and this process currently holds the active-instance lease. Its JSON includes the sanitized boolean `instanceLeaseHeld`. A process that is alive but does not hold the lease returns `503 Service Unavailable`.

`/livez` remains dependency-free and does not depend on lease ownership. It can therefore continue to return `200 OK` while `/healthz` is `503`, including during startup before acquisition or during fenced shutdown. This is intentional: use `/livez` for process liveness and `/healthz` for readiness/availability.

Operators should alert on repeated active-instance lease conflicts because they normally indicate an overlapping deployment or a previous process that has not finished its bounded handoff. Do not log or expose MongoDB connection strings, Discord tokens, or per-process holder IDs when diagnosing lease failures.
