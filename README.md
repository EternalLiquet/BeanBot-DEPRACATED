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

Run it with your `.env` file and a persistent volume for logs and runtime files:

```powershell
docker run -d `
  --name beanbot `
  --restart unless-stopped `
  --env-file .env `
  -v beanbot-data:/app/BeanBotFiles `
  beanbot
```

The container uses the .NET 8 runtime image because this is a console application, not an ASP.NET app.

## Note

This bot is slowly being replaced by a new implementation in Python. The .NET version will remain available for the foreseeable future, but no new features will be added to it. The Python version is still in early development and may not have all the same features yet. It will be transitioned one module at a time.
