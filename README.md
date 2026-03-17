[![Bean Bot Version](https://img.shields.io/github/v/release/EternalLiquet/BeanBot-DEPRACATED?display_name=tag&label=Bean%20Bot%20Version)](https://github.com/EternalLiquet/BeanBot-DEPRACATED/releases/latest)
[![.NET Build](https://github.com/EternalLiquet/BeanBot-DEPRACATED/actions/workflows/dotnetaction.yml/badge.svg?branch=master)](https://github.com/EternalLiquet/BeanBot-DEPRACATED/actions/workflows/dotnetaction.yml)

# Bean Bot

Bean Bot is a `.NET 10` Discord bot built on `NetCord`, with MongoDB-backed reaction-role storage, Serilog logging, and Docker support.

## Runtime Stack

- `.NET 10` console application
- `NetCord` gateway and command hosting
- `MongoDB.Driver` for persistent role reaction settings
- `Serilog` for structured logging
- direct `meme-api.com` HTTP integration for meme fetches

## Configuration

The bot no longer creates or reads `beanSettings.json`. Configuration comes from environment variables, and a local `.env` file is loaded automatically when present.

Copy [`.env.example`](/n:/Personal/DotNet/BeanBot-DEPRACATED/.env.example) to `.env` and fill in the values:

```env
BEANBOT_BOT_TOKEN=
BEANBOT_MONGO_CONNECTION_STRING=mongodb://mongo:27017
BEANBOT_GENERAL_CHANNEL_ID=
BEANBOT_HATOETE_URL=
BEANBOT_YOSHIMARU_URL=
BEANBOT_LOG_LEVEL=Information
BEANBOT_IL_SERVER_ID=
```

`BEANBOT_LOG_LEVEL` controls the minimum Serilog level. Typical values are `Debug`, `Information`, `Warning`, and `Error`.

Real environment variables take precedence over `.env` values.

## Local Run

```powershell
dotnet restore BeanBot.sln
dotnet build BeanBot.sln -c Release
dotnet run --project BeanBot/BeanBot.csproj
```

The repo includes [`global.json`](/n:/Personal/DotNet/BeanBot-DEPRACATED/global.json), so the intended SDK line is pinned automatically.

## Role Setup

Reaction-role setup is now batch-oriented and documented so you do not have to walk people through it manually.

Run `%rolesetting` inside a guild text channel where:

- the invoking user has `Manage Roles`
- the bot has `Send Messages`, `Embed Links`, `Add Reactions`, and `Manage Messages`
- the target roles are below the bot's highest role

The flow is:

1. Run `%rolesetting`.
2. Send the label for the role menu, or type `skip` to use `Role Selection`.
3. Send every mapping in a single message, one per line, using:

```text
<emoji> <role mention or exact role name>
```

Examples:

```text
❤️ @Announcements
🔥 Raid Team
<:party:123456789012345678> @Events
```

Notes:

- Standard emoji should be sent as the actual emoji character, such as `❤️`.
- Custom guild emoji can be sent as a real emoji mention like `<:party:123456789012345678>` or by exact guild emoji name.
- Shortcodes like `:heart:` are not expanded by the setup parser.
- The published role message stays up permanently; only the setup conversation is cleaned up.

Existing custom-emoji role menus remain compatible. Legacy Mongo documents that only stored `emojiId` are still resolved, and new role menus simply store an additional normalized emoji key so Unicode emoji can work too.

## In-Bot Help

The help command now supports module and command drill-down:

```text
%help
%help administration
%help rolesetting
```

`%help` shows all modules and their descriptions. From there, `%help <module>` shows the commands in that module, and `%help <command>` drills down into the specific command's usage, aliases, scope, and parameter shape.

## Docker

Build from the repo root:

```powershell
docker build -t beanbot .
```

Run it with your `.env` file and a persistent runtime volume:

```powershell
docker run -d `
  --name beanbot `
  --restart unless-stopped `
  --env-file .env `
  -v beanbot-data:/app/BeanBotFiles `
  beanbot
```

The container uses the `.NET 10` runtime image because this is a console bot, not an ASP.NET app.

## Notes

- The meme command now talks directly to the documented `meme-api.com/gimme` API instead of going through a third-party NuGet wrapper.
- The repo uses central package management through [`Directory.Packages.props`](/n:/Personal/DotNet/BeanBot-DEPRACATED/Directory.Packages.props).
