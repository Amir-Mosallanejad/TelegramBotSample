# TelegramBot (.NET 8)

A simple Telegram bot that forwards every user message to an admin and lets the admin
reply, with all messages persisted in a local SQLite database via EF Core.

## Features

- Text messages from regular users are stored and forwarded to the admin chat.
- `/send <chatId> <message>` (admin only) sends a message to any chat.
- `/start` and `/help` provide a short usage summary.
- Every message is written to the `Messages` table with a delivery flag.
- Long polling (no webhook, no public HTTPS endpoint required).
- Graceful shutdown on `Ctrl+C` / `SIGTERM` via the generic host.
- Configuration through a `.env` file (`DotNetEnv`).

## 1. Prerequisites

- .NET 8 SDK (`dotnet --version` should report 8.x)
- A bot token from [@BotFather](https://t.me/BotFather) (`/newbot`)

## 2. Configure

```bash
cp .env.example .env      # Windows PowerShell: copy .env.example .env
```

Then edit `.env`:

```ini
BOT_TOKEN=1234567890:AA_your_token_from_botfather
ADMIN_CHAT_ID=987654321
```

`BOT_TOKEN` and `ADMIN_CHAT_ID` are the only required values. Real environment variables
take precedence over `.env`, so you can override them for a single run:

```bash
BOT_TOKEN=... ADMIN_CHAT_ID=... dotnet run
```

### How to get a chat id

1. Open a chat with [@userinfobot](https://t.me/userinfobot) and press **Start**.
2. It replies with something like `Id: 987654321`.
3. Put that number in `ADMIN_CHAT_ID`.

To get *another* user's chat id, have them message the bot, then look at the log output
(`Update received: type=Message, chat=555, user=555`) or query the database. In groups
the chat id is negative, e.g. `-1001234567890`.

## 3. Packages

```bash
dotnet add package Telegram.Bot
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package DotNetEnv
dotnet add package Microsoft.Extensions.Hosting
```

Versions are pinned in `TelegramBot.csproj`:

| Package | Version | Why |
| --- | --- | --- |
| `Telegram.Bot` | 22.10.3.2 | Client, polling, `IUpdateHandler` |
| `Microsoft.EntityFrameworkCore.Sqlite` | 8.0.31 | Database (EF Core 8, last version supporting .NET 8) |
| `Microsoft.EntityFrameworkCore.Design` | 8.0.31 | Migrations CLI only, not shipped |
| `Microsoft.Extensions.Hosting` | 8.0.1 | DI, logging, configuration, shutdown |
| `DotNetEnv` | 3.2.0 | Loads `.env` into the process environment |

`Microsoft.Extensions.DependencyInjection` ships inside `Microsoft.Extensions.Hosting`,
so it is not listed separately.

## 4. Migrations

Install the EF CLI once (as a global tool):

```bash
dotnet tool install --global dotnet-ef --version 8.0.31
```

Then, from the project directory:

```bash
# Create a migration after changing Models/Message.cs or Data/BotDbContext.cs
dotnet ef migrations add InitialCreate --output-dir Data/Migrations

# Apply migrations (creates/updates bot.db)
dotnet ef database update

# Inspect state
dotnet ef migrations list
dotnet ef database update 0          # roll back all migrations
```

The bot also applies pending migrations automatically on startup, so
`dotnet ef database update` is only needed if you want to control it manually.

To delete the database and start over, remove `bot.db` and run the bot again.

## 5. Run

```bash
dotnet run
```

Expected startup output:

```
info: Program[0]
      Applying 1 pending migration(s)...
info: Program[0]
      Database migrated successfully.
info: TelegramBot.Services.BotService[0]
      Starting long polling...
info: Startup[0]
      Bot is running as @your_bot.
info: Startup[0]
      Admin chat id: 987654321
info: PollingBackgroundService[0]
      Polling worker started. Press Ctrl+C to stop the bot.
```

## 6. Test

1. Open Telegram, start your bot, and send `hello`.
2. The admin account receives a message containing the sender, the chat id and the text.
3. The user receives `Your message has been forwarded to the admin.`
4. From the admin chat send `/send <chatId> your answer`; the user receives it.
5. Check the stored rows:

```bash
dotnet run --no-build   # then inspect with any SQLite browser
```

Table `Messages` in `bot.db`:

| Id | SenderChatId | ReceiverChatId | MessageText | SentAt | IsDelivered |
| --- | --- | --- | --- | --- | --- |
| 1 | 555 | 999 | `<b>New message</b>...` | 2026-09-29 15:21:46Z | True |
| 2 | 999 | 999 | Admin just typing a note | 2026-09-29 15:21:47Z | True |
| 3 | 999 | 424242 | Reply from the admin | 2026-09-29 15:21:49Z | True |

`IsDelivered` is `false` when the Telegram API rejected the send (for example the bot
was blocked by the user); the row is kept so the message is not lost.

Press `Ctrl+C` to stop the bot. The host cancels the polling token and waits for the
receive loop to finish before exiting.

## Project layout

```
TelegramBot/
├── .env                     local secrets (git-ignored)
├── .env.example             template for .env
├── .gitignore
├── README.md
├── TelegramBot.csproj
├── Program.cs               host, DI, startup migration, shutdown
├── Configuration/
│   └── BotConfig.cs         BOT_TOKEN / ADMIN_CHAT_ID
├── Data/
│   ├── BotDbContext.cs      EF Core context (table Messages)
│   ├── BotDbContextFactory.cs  design-time factory for "dotnet ef"
│   └── Migrations/          generated by "dotnet ef migrations add"
├── Models/
│   └── Message.cs           entity
├── Services/
│   ├── BotService.cs        Telegram client, sending, polling
│   └── MessageService.cs    persistence + delivery
└── Handlers/
    └── UpdateHandler.cs     IUpdateHandler: commands and forwarding
```

## Notes on Telegram.Bot 22.10

22.10 renamed and reshaped parts of the API compared to older v22 releases:

| Older v22 | 22.10 (used here) |
| --- | --- |
| `TelegramClient` | `TelegramBotClient` |
| `ITelegramClient` | `ITelegramBotClient` |
| `SendMessageAsync(chatId: long, ...)` | `SendMessage(chatId: ChatId, ...)` |
| `StartPollingAsync(handler, ...)` | `ReceiveAsync(handler, receiverOptions, ct)` |
| `handler.HandleUpdateAsync(update, ct)` | `handler.HandleUpdateAsync(botClient, update, ct)` |

Shutdown is driven only by the `CancellationToken`; there is no `StopPolling` call.
