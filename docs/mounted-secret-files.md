# Mounted secret files

BeanBot can load its credential-bearing runtime settings either directly from configuration values or from files mounted into the process at startup.

Mounted files are supported only for the existing secret settings:

| Direct value | File-backed alternative |
| --- | --- |
| `BEANBOT_BOT_TOKEN` | `BEANBOT_BOT_TOKEN_FILE` |
| `BEANBOT_MONGO_CONNECTION_STRING` | `BEANBOT_MONGO_CONNECTION_STRING_FILE` |
| `BEANBOT_HEALTHCHECK_BEARER_TOKEN` | `BEANBOT_HEALTHCHECK_BEARER_TOKEN_FILE` |

Ordinary settings such as channel IDs, URLs, schedules, and feature flags remain normal configuration values.

## Choose one source per secret

For a given secret, configure either the direct value or its `*_FILE` alternative, never both. BeanBot fails startup when both sources are present instead of silently choosing one. This includes the legacy direct aliases such as `botToken` and `mongoConnectionString`.

Existing direct configuration remains fully supported:

```env
BEANBOT_BOT_TOKEN=replace-me
BEANBOT_MONGO_CONNECTION_STRING=mongodb://mongo:27017
BEANBOT_HEALTHCHECK_BEARER_TOKEN=replace-me-if-health-is-exposed
```

For mounted files, remove or comment out the matching direct variable and configure the file path instead:

```env
BEANBOT_BOT_TOKEN_FILE=/run/secrets/beanbot_bot_token
BEANBOT_MONGO_CONNECTION_STRING_FILE=/run/secrets/beanbot_mongo_connection
BEANBOT_HEALTHCHECK_BEARER_TOKEN_FILE=/run/secrets/beanbot_health_token
```

A blank direct variable still counts as a configured direct source, so do not leave `BEANBOT_BOT_TOKEN=` or another matching direct key in `.env` when using its `*_FILE` form.

The optional health bearer token is resolved only when the health listener is enabled with `BEANBOT_HEALTHCHECK_PORT`, matching the existing behavior for auxiliary health settings.

## Docker example

The production image already runs as a non-root application user. Mount each secret read-only and make sure that user can read the mounted file.

```powershell
docker run -d `
  --name beanbot `
  --restart unless-stopped `
  --stop-timeout 130 `
  --env-file .env `
  --mount type=bind,source=C:\beanbot-secrets\bot-token,target=/run/secrets/beanbot_bot_token,readonly `
  --mount type=bind,source=C:\beanbot-secrets\mongo-connection,target=/run/secrets/beanbot_mongo_connection,readonly `
  -e BEANBOT_BOT_TOKEN_FILE=/run/secrets/beanbot_bot_token `
  -e BEANBOT_MONGO_CONNECTION_STRING_FILE=/run/secrets/beanbot_mongo_connection `
  -v beanbot-data:/app/BeanBotFiles `
  beanbot
```

The same pattern works with Docker Compose, systemd credentials, Kubernetes-style secret volumes, or another mechanism that presents a normal readable file to the BeanBot process. BeanBot does not call Vault, SSM, Kubernetes, or another secret-manager API itself.

## Read and validation behavior

Secret files are read once while startup configuration is built. Updating a mounted file does not rotate a running BeanBot process; restart BeanBot to consume the new value.

BeanBot applies these bounds and validation rules:

- only the exact configured file path is opened; directories and globs are not scanned;
- at most 16 KiB is read for a secret;
- missing, unreadable, empty, oversized, invalid UTF-8, or NUL-containing files fail startup with sanitized diagnostics;
- one terminal LF or CRLF is removed for compatibility with common secret-mount tooling;
- other whitespace and secret characters are preserved rather than broadly trimmed;
- file handles are disposed after the startup read.

Failure messages identify the relevant configuration key but do not print the secret value or file contents.

Mounted files reduce incidental exposure through environment/configuration inspection, but they do not protect secrets from an administrator who already controls the host, container runtime, or process. Keep the mounted files readable by BeanBot's non-root user and apply host/runtime access controls appropriate for your deployment.
