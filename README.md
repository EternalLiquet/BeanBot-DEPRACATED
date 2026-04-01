[![Bean Bot Version](https://img.shields.io/github/v/release/EternalLiquet/BeanBot-DEPRACATED?display_name=tag&label=Bean%20Bot%20Version)](https://github.com/EternalLiquet/BeanBot-DEPRACATED/releases/latest)
[![.NET Core Master and Deploy Checks](https://github.com/EternalLiquet/BeanBot-DEPRACATED/actions/workflows/dotnetaction.yml/badge.svg?branch=master)](https://github.com/EternalLiquet/BeanBot-DEPRACATED/actions/workflows/dotnetaction.yml)

# Bean Bot

Bean Bot is a .NET 8 Discord bot with MongoDB-backed role reaction storage and a small set of server utilities.

## Configuration

The app no longer creates or reads `beanSettings.json`. Configuration is now supplied through environment variables, and the bot will also load a local `.env` file automatically when present. Copy `.env.example` to `.env` and fill in the values:

```env
BEANBOT_BOT_TOKEN=
BEANBOT_MONGO_CONNECTION_STRING=
BEANBOT_GENERAL_CHANNEL_ID=
BEANBOT_HATOETE_URL=
BEANBOT_YOSHIMARU_URL=
```

For backwards compatibility, the legacy variable names (`botToken`, `mongoConnectionString`, and so on) are still accepted, but the `BEANBOT_*` names are the intended format. Real environment variables take precedence over values from `.env`.

Optional health check settings:

```env
BEANBOT_HEALTHCHECK_PORT=8080
BEANBOT_HEALTHCHECK_BIND_ADDRESS=0.0.0.0
BEANBOT_HEALTHCHECK_BEARER_TOKEN=
BEANBOT_HEALTHCHECK_RATE_LIMIT_SECONDS=90
```

When `BEANBOT_HEALTHCHECK_PORT` is set, the bot exposes `GET /healthz` and `HEAD /healthz` on that port:

- `200 OK`: process is up and the Discord gateway session is ready.
- `503 Service Unavailable`: process is up, but Discord is not currently connected or ready.
- `429 Too Many Requests`: the same client polled again before the configured rate limit expired.
- no response / connection failure: the bot process is down or unreachable.

If you bind the endpoint to anything other than `127.0.0.1`, set `BEANBOT_HEALTHCHECK_BEARER_TOKEN` and send `Authorization: Bearer <token>` from Home Assistant.

## Local Run

```powershell
dotnet build BeanBot/BeanBot.csproj
dotnet run --project BeanBot/BeanBot.csproj
```

If a `.env` file exists in the repo root, `dotnet run` will load it automatically.

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
  --env-file .env `
  -p 8080:8080 `
  -v beanbot-data:/app/BeanBotFiles `
  beanbot
```

The container uses the .NET 8 runtime image because this is a console application, not an ASP.NET app.

## Note

This bot is slowly being replaced by a new implementation in Python. The .NET version will remain available for the foreseeable future, but no new features will be added to it. The Python version is still in early development and may not have all the same features yet. It will be transitioned one module at a time. The .NET version will continue to receive critical bug fixes and security updates as needed, but new features and improvements will be focused on the Python version going forward.
