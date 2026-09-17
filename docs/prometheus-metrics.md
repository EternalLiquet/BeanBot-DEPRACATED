# Prometheus metrics

BeanBot can expose a small, local Prometheus scrape surface on the existing health-check listener. Metrics are disabled by default and enabling them does not send telemetry anywhere.

## Enable metrics

Configure the existing health listener and opt in to metrics:

```env
BEANBOT_HEALTHCHECK_PORT=8080
BEANBOT_HEALTHCHECK_BIND_ADDRESS=127.0.0.1
BEANBOT_HEALTHCHECK_BEARER_TOKEN=replace-with-a-secret
BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS=90
BEANBOT_METRICS_ENABLED=true
```

`BEANBOT_METRICS_ENABLED=true` requires `BEANBOT_HEALTHCHECK_PORT`. BeanBot does not open another port or start another web host. The same Kestrel listener serves `/healthz`, `/livez`, and `/metrics`, with the same request limits, bounded client tracking, bearer-token policy, and shutdown ownership.

If the listener is bound beyond loopback, use a bearer token and restrict network access. `/metrics` intentionally contains only low-cardinality operational state, but it still reveals service availability and build/runtime behavior.

## Scrape configuration

The endpoint accepts only `GET` and `HEAD`. Prometheus scrapes should respect `BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS`; otherwise the listener returns `429 Too Many Requests`.

A minimal Prometheus job looks like:

```yaml
scrape_configs:
  - job_name: beanbot
    scrape_interval: 90s
    metrics_path: /metrics
    authorization:
      type: Bearer
      credentials: replace-with-a-secret
    static_configs:
      - targets: ['beanbot:8080']
```

Adjust the target and authentication to match the deployment. If the health listener is loopback-only, scrape it from the same host or through an explicitly secured local path rather than widening the bind address solely for metrics.

## Exported metrics

The initial metric set is intentionally small:

- `beanbot_discord_ready` — current Discord readiness as 0/1.
- `beanbot_discord_ready_transitions_total` — transitions into Gateway Ready.
- `beanbot_discord_disconnect_transitions_total` — transitions into disconnected state.
- `beanbot_discord_last_ready_timestamp_seconds` — Unix timestamp of the most recent transition into Ready, or 0 when none has occurred.
- `beanbot_discord_last_disconnect_timestamp_seconds` — Unix timestamp of the most recent transition into disconnected state, or 0 when none has occurred.
- `beanbot_mongo_reachable` — last observed MongoDB reachability as 0/1. Check `beanbot_mongo_state_known` before interpreting it.
- `beanbot_mongo_state_known` — 1 after BeanBot owns a completed or timed-out Mongo readiness result; 0 before the first observation.
- `beanbot_mongo_state_fresh` — whether the last observed Mongo readiness result remains inside the readiness freshness window.
- `beanbot_mongo_last_probe_timestamp_seconds` — Unix timestamp of the last observed Mongo readiness result, or 0 before one exists.
- `beanbot_mongo_probe_outcomes_total{result="success|failure|timeout"}` — probe outcomes using only the fixed three-value result label.

Metrics do not contain guild, channel, message, user, or role IDs; disconnect-reason text; exception text/types; command/user content; connection strings; hostnames; URLs; bearer tokens; or other arbitrary configuration values.

## Snapshot-only behavior

`/metrics` reports process-local observations that BeanBot already owns. A scrape does not:

- call Discord or enumerate Discord entities;
- start or wait for a MongoDB readiness probe;
- query a MongoDB collection;
- retry an operation;
- create a polling/background task;
- contact a collector or telemetry service.

Discord transition counters and transition timestamps are updated by the existing Gateway lifecycle state owner. Mongo counters and last-known state are updated only when the existing readiness monitor owns a probe result. Repeated scrapes only read those snapshots.

Before the first Mongo readiness result, Mongo metrics explicitly report an unknown/unfresh state instead of triggering a probe or assuming success.

## Health versus metrics

The three HTTP surfaces have intentionally different jobs:

- `/livez` is dependency-free process liveness and does not query Discord or MongoDB.
- `/healthz` is active readiness and may execute the existing bounded MongoDB readiness probe.
- `/metrics` is historical/snapshot observability and never initiates dependency work.

A failure to scrape metrics does not change readiness, and health checks do not depend on metrics being consumed. Use `/livez` for process restart decisions, `/healthz` for current application availability, and `/metrics` for trends and alerts over time.
