[![Bean Bot Version](https://img.shields.io/github/v/release/EternalLiquet/BeanBot-DEPRACATED?display_name=tag&label=Bean%20Bot%20Version)](https://github.com/EternalLiquet/BeanBot-DEPRACATED/releases/latest)
[![.NET Core Master and Deploy Checks](https://github.com/EternalLiquet/BeanBot-DEPRACATED/actions/workflows/dotnetaction.yml/badge.svg?branch=master)](https://github.com/EternalLiquet/BeanBot-DEPRACATED/actions/workflows/dotnetaction.yml)

# Bean Bot

Bean Bot is a .NET 10 Discord bot with MongoDB-backed role reaction storage and a small set of server utilities.

## Configuration

The app no longer creates or reads `beanSettings.json`. Configuration is now supplied through environment variables, and the bot will also load a local `.env` file automatically when present. Copy `.env.example` to `.env` and fill in the values:

```env
BEANBOT_BOT_TOKEN=
BEANBOT_MONGO_CONNECTION_STRING=
BEANBOT_GENERAL_CHANNEL_ID=
BEANBOT_HATOETE_URL=
BEANBOT_YOSHIMARU_URL=
```

Configuration is bound through the .NET configuration and Options pipeline and validated when the host starts. Missing or malformed settings stop startup with messages that name the affected variable without printing its value. For backwards compatibility, the legacy variable names (`botToken`, `mongoConnectionString`, and so on) are still accepted, but the `BEANBOT_*` names are the intended format. Canonical names take precedence over legacy aliases, and real environment variables take precedence over values from `.env`.

### Discord Gateway Intents

BeanBot explicitly requests only the gateway events used by its commands and event handlers. In the Discord Developer Portal, open the application, select **Bot**, and enable these privileged intents before deployment:

- **Server Members Intent** for new-member welcome messages and current guild-member data.
- **Message Content Intent** for BeanBot's prefix commands and edited-message handling.

Leave **Presence Intent** disabled; BeanBot does not use presence events. The remaining requested intents are unprivileged and cover guild/channel state, guild emotes, guild and direct messages, and guild/direct-message reactions used by reaction roles and pagination.

Optional health check settings:

```env
BEANBOT_HEALTHCHECK_PORT=8080
BEANBOT_HEALTHCHECK_BIND_ADDRESS=0.0.0.0
BEANBOT_HEALTHCHECK_BEARER_TOKEN=
BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS=90
```

When `BEANBOT_HEALTHCHECK_PORT` is set, the bot exposes a Kestrel-hosted `GET /healthz` and `HEAD /healthz` endpoint on that port:

- `200 OK`: process is up and the Discord gateway session is ready.
- `401 Unauthorized`: the bearer token is missing or invalid.
- `503 Service Unavailable`: process is up, but Discord is not currently connected or ready.
- `429 Too Many Requests`: the same client polled again before the configured rate limit expired.
- no response / connection failure: the bot process is down or unreachable.

Successful and unhealthy JSON responses also include the non-secret release version and Git commit SHA so an operator can identify the running image.

If you bind the endpoint to anything other than `127.0.0.1`, set `BEANBOT_HEALTHCHECK_BEARER_TOKEN` and send `Authorization: Bearer <token>` from Home Assistant.

The same listener also exposes dependency-free `GET /livez` and `HEAD /livez` liveness checks. `/livez` returns `200 OK` with only the non-secret build identity while the BeanBot process and Kestrel health surface can answer; it does not query Discord, MongoDB, external services, or persistence. It uses the same bearer-token policy, Kestrel limits, bounded client tracking, and poll interval as `/healthz` without opening another port.

Use `/livez` for process/container liveness and restart decisions, and use `/healthz` for application readiness/availability monitoring. A recoverable required-dependency outage may therefore produce `/healthz = 503` while `/livez = 200`; that divergence is expected. A supervisor should not restart BeanBot solely because readiness is temporarily unavailable unless its operational policy deliberately chooses to do so.

## Local Development

Install a stable .NET 10 SDK and Docker. The repository's `global.json` accepts SDK
10.0.100 or newer .NET 10 patches and feature bands while excluding preview and
other major SDKs. The integration suite automatically starts an isolated MongoDB
container; it does not use `BEANBOT_MONGO_CONNECTION_STRING` or require a manually
managed test database. Then restore, build, and run the test suite from the repo root:

```powershell
dotnet restore BeanBot.sln
dotnet build BeanBot.sln --configuration Release --no-restore
dotnet test BeanBot.sln --configuration Release --no-build
```

Repository changes use a Codex-native Planner → Implementer → Verifier → Reviewer loop with one writer and independent verification/review. See [Codex development loop](docs/codex-development-loop.md) for role handoffs and the shared fast/full verification commands.

### Source layout

BeanBot remains a single application project, organized by responsibility:

- `Configuration` binds and validates runtime settings.
- `Discord` contains commands, event handlers, gateway lifecycle, messaging helpers, and reaction-role behavior.
- `Health` owns gateway health snapshots and the authenticated `/healthz` endpoint.
- `Hosting` composes the Generic Host and coordinates startup and shutdown.
- `Logging` contains structured log messages and Discord owner-alert delivery.
- `Persistence` contains runtime directory setup, persisted models, outage state, and MongoDB repositories.

Tests mirror these production responsibilities where useful, with cross-component scenarios kept under `BeanBot.Tests/Integration`.

Repository-wide compiler settings are defined in `Directory.Build.props`, package
versions in `Directory.Packages.props`, and formatting and naming conventions in
`.editorconfig`. Run `./scripts/verify.sh fast` before submitting changes; it checks
formatting and analyzers in addition to building and testing the solution.
Full verification additionally enforces locked restores, the measured coverage
baseline, master/develop ancestry, dependency vulnerability checks, a
digest-pinned Docker build, and a non-root/read-only container smoke test.
Coverage reports are written under `.artifacts/coverage`.

To start the bot:

```powershell
dotnet run --project BeanBot/BeanBot.csproj
```

The bot requires access to the MongoDB instance configured by `BEANBOT_MONGO_CONNECTION_STRING`. If a `.env` file exists in the repo root, `dotnet run` loads it automatically.
BeanBot runs through the .NET Generic Host, so Ctrl+C and normal process-stop signals trigger the same bounded graceful-shutdown path used in production.

## Docker

Build the image from the repo root:

```powershell
docker build -t beanbot .
```

Run it with your `.env` file and a persistent volume for logs and runtime files. If you enable `BEANBOT_HEALTHCHECK_PORT=8080`, publish that port as well:

```powershell
docker run -d `
  --name beanbot `
  --restart unless-stopped `
  --stop-timeout 130 `
  --env-file .env `
  -p 8080:8080 `
  -v beanbot-data:/app/BeanBotFiles `
  beanbot
```

The container uses the digest-pinned .NET 10 Noble chiseled-extra ASP.NET runtime,
runs as the image's non-root application user, and provides the Kestrel health endpoint.
Container stop signals are handled by the Generic Host and flow through BeanBot's bounded Discord and background-service shutdown sequence.

For hardened runtime flags, GHCR digest deployment, host bind-mount ownership,
release validation, and rollback, see [Release readiness and operations](docs/release-readiness.md).

BeanBot makes three bounded attempts to log in to Discord during startup. Permanent token failures fail immediately; exhausted transient failures exit with a non-zero status so the configured Docker restart policy can start a fresh process. Runtime gateway disconnects continue to use BeanBot's separate natural-recovery, manual-reconnect, and process-restart sequence.

## Note

This bot is slowly being replaced by a new implementation in Python. The .NET version will remain available for the foreseeable future, but no new features will be added to it. The Python version is still in early development and may not have all the same features yet. It will be transitioned one module at a time. The .NET version will continue to receive critical bug fixes and security updates as needed, but new features and improvements will be focused on the Python version going forward.
