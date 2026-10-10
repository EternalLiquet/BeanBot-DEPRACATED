# Deployment preflight

BeanBot provides an explicit offline preflight mode for checking the configuration and local runtime prerequisites of the exact executable or container image that is about to be deployed.

Run the published application directly with:

```text
dotnet BeanBot.dll --preflight
```

For the normal container deployment, use the same environment file and persistent volume that the real BeanBot process will use:

```text
docker run --rm \
  --env-file .env \
  -v beanbot-data:/app/BeanBotFiles \
  beanbot --preflight
```

A successful preflight exits with status `0` and prints the BeanBot version and commit SHA. Any required validation failure exits non-zero.

## What preflight checks

Preflight loads configuration through the same `.env` / environment normalization path as normal startup, runs the production `BeanBotSettings` validator, and constructs the same immutable runtime options used by the application. It then performs only local checks:

- required configuration is present and well formed;
- Discord snowflake IDs, configured HTTP/HTTPS URLs, daily-pun schedule/timezone, health settings, and new-member welcome settings satisfy the production validation rules;
- the MongoDB connection string is syntactically parseable by the pinned MongoDB driver;
- `Resources/puns.csv` exists and is non-empty;
- the configured `BeanBotFiles` directory can be created/accessed and written by the current process user.

The filesystem check writes one small uniquely named temporary probe beneath `BeanBotFiles` and removes it before preflight returns. Failed probes also attempt cleanup, so repeated checks do not intentionally leave a growing trail of files.

Diagnostics name the failed setting or local prerequisite where possible, but do not print the Discord token, MongoDB connection string, health bearer token, or a complete configuration/environment dump.

## What preflight deliberately does not prove

Preflight is offline. It does **not**:

- log in to Discord or validate that the bot token is accepted;
- register application commands, subscribe event handlers, or start BeanBot background services;
- connect to, ping, query, or write MongoDB;
- fetch configured media URLs or make other outbound HTTP requests;
- bind the configured Kestrel health port;
- mutate reaction-role, role-menu, outage, or daily-pun persistence.

A passing preflight therefore proves only that local configuration and packaged/runtime prerequisites are coherent. It does not prove that Discord, MongoDB, DNS, or the internet are currently reachable.

Use preflight immediately before replacing a running container when practical, but keep the normal release-candidate container smoke tests, `/healthz` readiness monitoring, `/livez` liveness monitoring, and deployment rollback procedures. Preflight is an explicit operator/CI command; normal BeanBot startup does not automatically run a second preflight subprocess.
